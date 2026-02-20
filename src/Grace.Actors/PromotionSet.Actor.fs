namespace Grace.Actors

open Azure.Storage.Blobs
open Azure.Storage.Blobs.Specialized
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Services
open Grace.Shared.Utilities
open Grace.Types.Artifact
open Grace.Types.Events
open Grace.Types.PromotionSet
open Grace.Types.Queue
open Grace.Types.Reference
open Grace.Types.Reminder
open Grace.Types.Types
open Grace.Types.Validation
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.IO.Compression
open System.Text
open System.Threading.Tasks

module PromotionSet =

    type private RecomputeFailure =
        | Blocked of reason: string * artifactId: ArtifactId option
        | Failed of reason: string

    type private DirectorySnapshot = { DirectoriesByPath: Dictionary<RelativePath, DirectoryVersion>; FilesByPath: Dictionary<RelativePath, FileVersion> }

    type private StepConflictFile =
        {
            FilePath: RelativePath
            BaseFile: FileVersion option
            OursFile: FileVersion option
            TheirsFile: FileVersion option
            IsBinary: bool
        }

    type PromotionSetActor([<PersistentState(StateName.PromotionSet, Constants.GraceActorStorage)>] state: IPersistentState<List<PromotionSetEvent>>) =
        inherit Grain()

        static let actorName = ActorName.PromotionSet
        let log = loggerFactory.CreateLogger("PromotionSet.Actor")

        let mutable currentCommand = String.Empty
        let mutable promotionSetDto = PromotionSetDto.Default

        let getIntEnvironmentSetting (name: string) (defaultValue: int) =
            let rawValue = Environment.GetEnvironmentVariable(name)
            let mutable parsedValue = 0

            if String.IsNullOrWhiteSpace rawValue |> not
               && Int32.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, &parsedValue)
               && parsedValue > 0 then
                parsedValue
            else
                defaultValue

        let maxStepsPerRecompute = getIntEnvironmentSetting "grace__promotionset__recompute__max_steps" 1000

        let maxStepTimeMilliseconds = getIntEnvironmentSetting "grace__promotionset__recompute__max_step_time_ms" 30000

        let maxTotalTimeMilliseconds = getIntEnvironmentSetting "grace__promotionset__recompute__max_total_time_ms" 300000

        let maxFilesPerStep = getIntEnvironmentSetting "grace__promotionset__recompute__max_files_per_step" 20000

        member val private correlationId: CorrelationId = String.Empty with get, set

        member private this.WithActorMetadata(metadata: EventMetadata) =
            let properties = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

            metadata.Properties
            |> Seq.iter (fun kvp -> properties[kvp.Key] <- kvp.Value)

            if promotionSetDto.RepositoryId <> RepositoryId.Empty then
                properties[nameof RepositoryId] <- $"{promotionSetDto.RepositoryId}"

            let actorId =
                if promotionSetDto.PromotionSetId
                   <> PromotionSetId.Empty then
                    $"{promotionSetDto.PromotionSetId}"
                else
                    $"{this.GetPrimaryKey()}"

            properties["ActorId"] <- actorId

            let principal =
                if String.IsNullOrWhiteSpace metadata.Principal then
                    GraceSystemUser
                else
                    metadata.Principal

            { metadata with Principal = principal; Properties = properties }

        member private this.BuildSuccess(message: string, correlationId: CorrelationId) =
            let graceReturnValue: GraceReturnValue<string> =
                (GraceReturnValue.Create message correlationId)
                    .enhance(nameof RepositoryId, promotionSetDto.RepositoryId)
                    .enhance(nameof PromotionSetId, promotionSetDto.PromotionSetId)
                    .enhance ("Status", getDiscriminatedUnionCaseName promotionSetDto.Status)

            Ok graceReturnValue

        member private this.BuildError(errorMessage: string, correlationId: CorrelationId) =
            Error(
                (GraceError.Create errorMessage correlationId)
                    .enhance(nameof RepositoryId, promotionSetDto.RepositoryId)
                    .enhance (nameof PromotionSetId, promotionSetDto.PromotionSetId)
            )

        member private this.GetCurrentTerminalPromotion() =
            task {
                let! latestPromotion = getLatestPromotion promotionSetDto.RepositoryId promotionSetDto.TargetBranchId

                match latestPromotion with
                | Option.Some promotion -> return promotion.ReferenceId, promotion.DirectoryId
                | Option.None ->
                    let branchActorProxy = Branch.CreateActorProxy promotionSetDto.TargetBranchId promotionSetDto.RepositoryId this.correlationId
                    let! branchDto = branchActorProxy.Get this.correlationId

                    let baseDirectoryVersionId =
                        if branchDto.BasedOn.ReferenceId <> ReferenceId.Empty then
                            branchDto.BasedOn.DirectoryId
                        elif branchDto.LatestPromotion.ReferenceId
                             <> ReferenceId.Empty then
                            branchDto.LatestPromotion.DirectoryId
                        elif branchDto.LatestReference.ReferenceId
                             <> ReferenceId.Empty then
                            branchDto.LatestReference.DirectoryId
                        else
                            DirectoryVersionId.Empty

                    return ReferenceId.Empty, baseDirectoryVersionId
            }

        member private this.GetConflictResolutionPolicy() =
            task {
                let repositoryActorProxy = Repository.CreateActorProxy promotionSetDto.OrganizationId promotionSetDto.RepositoryId this.correlationId
                let! repositoryDto = repositoryActorProxy.Get this.correlationId
                return repositoryDto.ConflictResolutionPolicy
            }

        member private this.GetRepositoryDto() =
            task {
                let repositoryActorProxy = Repository.CreateActorProxy promotionSetDto.OrganizationId promotionSetDto.RepositoryId this.correlationId
                return! repositoryActorProxy.Get this.correlationId
            }

        member private this.UploadArtifactPayload(blobPath: string, payloadJson: string, metadata: EventMetadata) =
            task {
                try
                    let! repositoryDto = this.GetRepositoryDto()
                    let! uploadUri = getUriWithWriteSharedAccessSignature repositoryDto blobPath metadata.CorrelationId
                    let blockBlobClient = BlockBlobClient(uploadUri)
                    let payloadBytes = Encoding.UTF8.GetBytes(payloadJson)
                    use payloadStream = new MemoryStream(payloadBytes)
                    do! blockBlobClient.UploadAsync(payloadStream) :> Task
                    return Ok()
                with
                | ex ->
                    let graceError = GraceError.CreateWithException ex "Failed while uploading Artifact payload." metadata.CorrelationId

                    graceError.enhance (nameof PromotionSetId, promotionSetDto.PromotionSetId)
                    |> ignore

                    graceError.enhance (nameof RepositoryId, promotionSetDto.RepositoryId)
                    |> ignore

                    return Error(graceError)
            }

        member private this.GetArtifactText(artifactId: ArtifactId, metadata: EventMetadata) =
            task {
                let artifactActorProxy = Artifact.CreateActorProxy artifactId promotionSetDto.RepositoryId this.correlationId
                let! artifact = artifactActorProxy.Get this.correlationId

                match artifact with
                | Option.None ->
                    let graceError = GraceError.Create "Manual override artifact was not found." metadata.CorrelationId

                    graceError.enhance (nameof ArtifactId, artifactId)
                    |> ignore

                    graceError.enhance (nameof PromotionSetId, promotionSetDto.PromotionSetId)
                    |> ignore

                    return Error(graceError)
                | Option.Some artifactMetadata ->
                    try
                        let! repositoryDto = this.GetRepositoryDto()
                        let! downloadUri = getUriWithReadSharedAccessSignature repositoryDto artifactMetadata.BlobPath metadata.CorrelationId
                        let blobClient = BlobClient(downloadUri)
                        let! downloadResult = blobClient.DownloadContentAsync()
                        return Ok(downloadResult.Value.Content.ToString())
                    with
                    | ex ->
                        let graceError = GraceError.CreateWithException ex "Failed while downloading manual override artifact payload." metadata.CorrelationId

                        graceError.enhance (nameof ArtifactId, artifactId)
                        |> ignore

                        graceError.enhance (nameof PromotionSetId, promotionSetDto.PromotionSetId)
                        |> ignore

                        return Error(graceError)
            }

        member private this.ValidateManualOverrideArtifacts(decisions: ConflictResolutionDecision list, metadata: EventMetadata) =
            task {
                let overrideArtifactIds =
                    decisions
                    |> List.choose (fun decision -> if decision.Accepted then decision.OverrideContentArtifactId else Option.None)
                    |> List.distinct

                let mutable validationError: GraceError option = Option.None
                let mutable index = 0

                while index < overrideArtifactIds.Length
                      && validationError.IsNone do
                    let artifactId = overrideArtifactIds[index]
                    let! artifactTextResult = this.GetArtifactText(artifactId, metadata)

                    match artifactTextResult with
                    | Error graceError -> validationError <- Some graceError
                    | Ok artifactText ->
                        if String.IsNullOrWhiteSpace artifactText then
                            let graceError = GraceError.Create "Manual override artifact content is empty." metadata.CorrelationId

                            graceError.enhance (nameof ArtifactId, artifactId)
                            |> ignore

                            graceError.enhance (nameof PromotionSetId, promotionSetDto.PromotionSetId)
                            |> ignore

                            validationError <- Some graceError

                    index <- index + 1

                match validationError with
                | Some graceError -> return Error graceError
                | Option.None -> return Ok()
            }

        member private this.HydrateStepProvenance(step: PromotionSetStep) =
            task {
                let referenceActorProxy = Reference.CreateActorProxy step.OriginalPromotion.ReferenceId promotionSetDto.RepositoryId this.correlationId
                let! promotionReferenceDto = referenceActorProxy.Get this.correlationId

                let promotionDirectoryVersionId =
                    if promotionReferenceDto.ReferenceId
                       <> ReferenceId.Empty then
                        promotionReferenceDto.DirectoryId
                    else
                        step.OriginalPromotion.DirectoryVersionId

                if promotionDirectoryVersionId = DirectoryVersionId.Empty then
                    return
                        Error(
                            (GraceError.Create "Original promotion did not include a directory version." this.correlationId)
                                .enhance (nameof PromotionSetStepId, step.StepId)
                        )
                else
                    let basedOnReferenceId =
                        if step.OriginalBasePromotionReferenceId
                           <> ReferenceId.Empty then
                            step.OriginalBasePromotionReferenceId
                        elif promotionReferenceDto.ReferenceId
                             <> ReferenceId.Empty then
                            promotionReferenceDto.Links
                            |> Seq.tryPick (fun link ->
                                match link with
                                | ReferenceLinkType.BasedOn referenceId -> Option.Some referenceId
                                | _ -> Option.None)
                            |> Option.defaultValue ReferenceId.Empty
                        else
                            ReferenceId.Empty

                    let! basedOnDirectoryVersionId =
                        if step.OriginalBaseDirectoryVersionId
                           <> DirectoryVersionId.Empty then
                            Task.FromResult step.OriginalBaseDirectoryVersionId
                        elif basedOnReferenceId <> ReferenceId.Empty then
                            task {
                                let basedOnReferenceActorProxy = Reference.CreateActorProxy basedOnReferenceId promotionSetDto.RepositoryId this.correlationId

                                let! basedOnReferenceDto = basedOnReferenceActorProxy.Get this.correlationId

                                if basedOnReferenceDto.ReferenceId = ReferenceId.Empty then
                                    return promotionDirectoryVersionId
                                else
                                    return basedOnReferenceDto.DirectoryId
                            }
                        else
                            Task.FromResult promotionDirectoryVersionId

                    return
                        Ok
                            { step with
                                OriginalPromotion = { step.OriginalPromotion with DirectoryVersionId = promotionDirectoryVersionId }
                                OriginalBasePromotionReferenceId = basedOnReferenceId
                                OriginalBaseDirectoryVersionId = basedOnDirectoryVersionId
                            }
            }

        member private this.GetModelConfidence(step: PromotionSetStep, computedAgainstBaseDirectoryVersionId: DirectoryVersionId, conflictCount: int) =
            if conflictCount <= 0 then
                1.0
            elif step.OriginalBaseDirectoryVersionId = computedAgainstBaseDirectoryVersionId then
                0.99
            elif conflictCount = 1 then
                0.93
            elif conflictCount = 2 then
                0.82
            else
                0.51

        member private this.TryGetFileVersion(fileLookup: Dictionary<RelativePath, FileVersion>, filePath: RelativePath) =
            let mutable fileVersion = FileVersion.Default

            if fileLookup.TryGetValue(filePath, &fileVersion) then
                Option.Some fileVersion
            else
                Option.None

        member private this.FileVersionEquivalent(left: FileVersion option, right: FileVersion option) =
            match left, right with
            | Option.None, Option.None -> true
            | Option.Some leftFile, Option.Some rightFile -> leftFile.Sha256Hash = rightFile.Sha256Hash
            | _ -> false

        member private this.LoadDirectorySnapshot(directoryVersionId: DirectoryVersionId) =
            task {
                let directoriesByPath = Dictionary<RelativePath, DirectoryVersion>(StringComparer.OrdinalIgnoreCase)
                let filesByPath = Dictionary<RelativePath, FileVersion>(StringComparer.OrdinalIgnoreCase)

                if directoryVersionId = DirectoryVersionId.Empty then
                    return Ok { DirectoriesByPath = directoriesByPath; FilesByPath = filesByPath }
                else
                    let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy directoryVersionId promotionSetDto.RepositoryId this.correlationId
                    let! rootDirectoryVersion = directoryVersionActorProxy.Get this.correlationId

                    if rootDirectoryVersion.DirectoryVersion.DirectoryVersionId = DirectoryVersionId.Empty then
                        let graceError = GraceError.Create "DirectoryVersion was not found while recomputing PromotionSet step." this.correlationId

                        graceError.enhance (nameof DirectoryVersionId, directoryVersionId)
                        |> ignore

                        graceError.enhance (nameof PromotionSetId, promotionSetDto.PromotionSetId)
                        |> ignore

                        return Error graceError
                    else
                        let! recursiveDirectoryVersions = directoryVersionActorProxy.GetRecursiveDirectoryVersions false this.correlationId
                        let mutable directoryIndex = 0

                        while directoryIndex < recursiveDirectoryVersions.Length do
                            let directoryVersion =
                                recursiveDirectoryVersions[directoryIndex]
                                    .DirectoryVersion

                            directoriesByPath[directoryVersion.RelativePath] <- directoryVersion
                            let filesInDirectory = directoryVersion.Files
                            let mutable fileIndex = 0

                            while fileIndex < filesInDirectory.Count do
                                let fileVersion = filesInDirectory[fileIndex]
                                filesByPath[fileVersion.RelativePath] <- fileVersion
                                fileIndex <- fileIndex + 1

                            directoryIndex <- directoryIndex + 1

                        return Ok { DirectoriesByPath = directoriesByPath; FilesByPath = filesByPath }
            }

        member private this.ReadTextFileVersion(repositoryDto, fileVersion: FileVersion option, correlationId: CorrelationId) =
            task {
                match fileVersion with
                | Option.None -> return Ok Option.None
                | Option.Some currentFileVersion when currentFileVersion.IsBinary -> return Ok Option.None
                | Option.Some currentFileVersion ->
                    try
                        let! readUri = getUriWithReadSharedAccessSignatureForFileVersion repositoryDto currentFileVersion correlationId
                        let blobClient = BlockBlobClient(readUri)
                        use! blobStream = blobClient.OpenReadAsync(position = 0, bufferSize = (64 * 1024))

                        use contentStream = new GZipStream(stream = blobStream, mode = CompressionMode.Decompress, leaveOpen = false) :> Stream

                        use streamReader = new StreamReader(contentStream, Encoding.UTF8, true, 16 * 1024, leaveOpen = false)
                        let! textContents = streamReader.ReadToEndAsync()
                        return Ok(Option.Some textContents)
                    with
                    | ex ->
                        return
                            Error(
                                (GraceError.CreateWithException ex "Failed while reading conflicted text file for PromotionSet recompute." correlationId)
                                    .enhance(nameof PromotionSetId, promotionSetDto.PromotionSetId)
                                    .enhance ("FilePath", currentFileVersion.RelativePath)
                            )
            }

        member private this.CreateTextOverrideFileVersion(filePath: RelativePath, textContent: string, repositoryDto, metadata: EventMetadata) =
            task {
                try
                    let payloadBytes = Encoding.UTF8.GetBytes(textContent)
                    use hashStream = new MemoryStream(payloadBytes)
                    let! sha256Hash = computeSha256ForFile hashStream filePath

                    let fileVersion = FileVersion.Create filePath sha256Hash String.Empty false (int64 payloadBytes.Length)

                    let! writeUri = getUriWithWriteSharedAccessSignatureForFileVersion repositoryDto fileVersion metadata.CorrelationId
                    let blobClient = BlobClient(writeUri)
                    use uploadStream = new MemoryStream()

                    use gzipStream = new GZipStream(uploadStream, CompressionLevel.Optimal, leaveOpen = true)
                    gzipStream.Write(payloadBytes, 0, payloadBytes.Length)
                    gzipStream.Flush()
                    gzipStream.Dispose()

                    uploadStream.Position <- 0L
                    do! blobClient.UploadAsync(uploadStream, overwrite = true) :> Task
                    return Ok fileVersion
                with
                | ex ->
                    return
                        Error(
                            (GraceError.CreateWithException ex "Failed while creating manual override file version." metadata.CorrelationId)
                                .enhance(nameof PromotionSetId, promotionSetDto.PromotionSetId)
                                .enhance ("FilePath", filePath)
                        )
            }

        member private this.ReadConflictTextPair(repositoryDto, conflictFile: StepConflictFile) =
            task {
                let! oursTextResult = this.ReadTextFileVersion(repositoryDto, conflictFile.OursFile, this.correlationId)

                match oursTextResult with
                | Error graceError -> return Error graceError
                | Ok oursTextOption ->
                    let! theirsTextResult = this.ReadTextFileVersion(repositoryDto, conflictFile.TheirsFile, this.correlationId)

                    match theirsTextResult with
                    | Error graceError -> return Error graceError
                    | Ok theirsTextOption ->
                        let oursContent =
                            match oursTextOption with
                            | Option.Some text -> text
                            | Option.None ->
                                match conflictFile.OursFile with
                                | Option.Some fileVersion when fileVersion.IsBinary -> $"<binary sha={fileVersion.Sha256Hash}>"
                                | Option.Some _ -> "<text unavailable>"
                                | Option.None -> "<deleted>"

                        let theirsContent =
                            match theirsTextOption with
                            | Option.Some text -> text
                            | Option.None ->
                                match conflictFile.TheirsFile with
                                | Option.Some fileVersion when fileVersion.IsBinary -> $"<binary sha={fileVersion.Sha256Hash}>"
                                | Option.Some _ -> "<text unavailable>"
                                | Option.None -> "<deleted>"

                        return Ok(oursContent, theirsContent)
            }

        member private this.BuildConflictAnalyses
            (
                repositoryDto,
                conflictFiles: StepConflictFile list,
                confidence: float option,
                resolutionMethod: ConflictResolutionMethod,
                acceptedFilePaths: HashSet<RelativePath>
            ) =
            task {
                let! conflictTextResults =
                    conflictFiles
                    |> List.map (fun conflictFile ->
                        task {
                            let! conflictTextResult = this.ReadConflictTextPair(repositoryDto, conflictFile)
                            return conflictFile, conflictTextResult
                        })
                    |> Task.WhenAll

                let firstError =
                    conflictTextResults
                    |> Seq.tryPick (fun (_, result) ->
                        match result with
                        | Error graceError -> Option.Some graceError
                        | Ok _ -> Option.None)

                match firstError with
                | Option.Some graceError -> return Error graceError
                | Option.None ->
                    let conflictAnalyses = ResizeArray<ConflictAnalysis>()

                    conflictTextResults
                    |> Seq.iter (fun (conflictFile, result) ->
                        match result with
                        | Error _ -> ()
                        | Ok (oursContent, theirsContent) ->
                            let lineCount =
                                max
                                    1
                                    (max
                                        (if String.IsNullOrEmpty oursContent then 0 else oursContent.Split('\n').Length)
                                        (if String.IsNullOrEmpty theirsContent then
                                             0
                                         else
                                             theirsContent.Split('\n').Length))

                            let acceptedByManualReview = acceptedFilePaths.Contains(conflictFile.FilePath)

                            let proposedResolution =
                                match confidence with
                                | Option.Some score ->
                                    Option.Some
                                        {
                                            ModelResolution =
                                                if acceptedByManualReview then
                                                    "Manual resolution accepted during conflict review."
                                                else
                                                    "Model-suggested merge proposal."
                                            Confidence = score
                                            Accepted = if acceptedByManualReview then Option.Some true else Option.None
                                        }
                                | Option.None when acceptedByManualReview ->
                                    Option.Some
                                        {
                                            ModelResolution = "Manual resolution accepted during conflict review."
                                            Confidence = 1.0
                                            Accepted = Option.Some true
                                        }
                                | Option.None -> Option.None

                            conflictAnalyses.Add(
                                {
                                    FilePath = conflictFile.FilePath
                                    OriginalHunks =
                                        [
                                            { StartLine = 1; EndLine = lineCount; OursContent = oursContent; TheirsContent = theirsContent }
                                        ]
                                    ProposedResolution = proposedResolution
                                    ResolutionMethod = resolutionMethod
                                }
                            ))

                    return Ok(conflictAnalyses |> Seq.toList)
            }

        member private this.MaterializeMergedDirectoryVersion
            (
                repositoryDto,
                baseSnapshot: DirectorySnapshot,
                oursSnapshot: DirectorySnapshot,
                theirsSnapshot: DirectorySnapshot,
                mergedFilesByPath: Dictionary<RelativePath, FileVersion>,
                metadata: EventMetadata
            ) =
            task {
                let directoryPaths = HashSet<RelativePath>(StringComparer.OrdinalIgnoreCase)
                directoryPaths.Add(RootDirectoryPath) |> ignore
                let filesByDirectory = Dictionary<RelativePath, ResizeArray<FileVersion>>(StringComparer.OrdinalIgnoreCase)
                let mergedFileValues = mergedFilesByPath.Values |> Seq.toArray
                let mutable fileIndex = 0

                while fileIndex < mergedFileValues.Length do
                    let fileVersion = mergedFileValues[fileIndex]
                    let fileDirectoryPath = getRelativeDirectory fileVersion.RelativePath RootDirectoryPath
                    let mutable currentDirectoryPath = fileDirectoryPath
                    let mutable continueUpTree = true

                    while continueUpTree do
                        directoryPaths.Add(currentDirectoryPath) |> ignore

                        match getParentPath currentDirectoryPath with
                        | Option.Some parentPath -> currentDirectoryPath <- parentPath
                        | Option.None -> continueUpTree <- false

                    let mutable filesInDirectory = Unchecked.defaultof<ResizeArray<FileVersion>>

                    if
                        not
                        <| filesByDirectory.TryGetValue(fileDirectoryPath, &filesInDirectory)
                    then
                        filesInDirectory <- ResizeArray<FileVersion>()
                        filesByDirectory[fileDirectoryPath] <- filesInDirectory

                    filesInDirectory.Add(fileVersion)
                    fileIndex <- fileIndex + 1

                let orderedDirectoryPaths =
                    directoryPaths
                    |> Seq.sortByDescending countSegments
                    |> Seq.toArray

                let childDirectoriesByParent = Dictionary<RelativePath, ResizeArray<RelativePath>>(StringComparer.OrdinalIgnoreCase)
                let mutable directoryPathIndex = 0

                while directoryPathIndex < orderedDirectoryPaths.Length do
                    let directoryPath = orderedDirectoryPaths[directoryPathIndex]

                    match getParentPath directoryPath with
                    | Option.Some parentPath ->
                        let mutable childDirectories = Unchecked.defaultof<ResizeArray<RelativePath>>

                        if
                            not
                            <| childDirectoriesByParent.TryGetValue(parentPath, &childDirectories)
                        then
                            childDirectories <- ResizeArray<RelativePath>()
                            childDirectoriesByParent[parentPath] <- childDirectories

                        childDirectories.Add(directoryPath)
                    | Option.None -> ()

                    directoryPathIndex <- directoryPathIndex + 1

                let computedDirectoryMetadata = Dictionary<RelativePath, DirectoryVersionId * Sha256Hash>(StringComparer.OrdinalIgnoreCase)
                let directoryVersionsToCreate = ResizeArray<DirectoryVersion>()
                let mutable materializationError: GraceError option = Option.None
                let mutable buildIndex = 0

                while buildIndex < orderedDirectoryPaths.Length
                      && materializationError.IsNone do
                    let directoryPath = orderedDirectoryPaths[buildIndex]
                    let mutable childDirectoryPaths = Unchecked.defaultof<ResizeArray<RelativePath>>

                    let childDirectoryPathsArray =
                        if childDirectoriesByParent.TryGetValue(directoryPath, &childDirectoryPaths) then
                            childDirectoryPaths
                            |> Seq.sortBy id
                            |> Seq.toArray
                        else
                            Array.Empty<RelativePath>()

                    let localChildDirectories = List<LocalDirectoryVersion>()
                    let childDirectoryIds = List<DirectoryVersionId>()
                    let mutable childIndex = 0

                    while childIndex < childDirectoryPathsArray.Length do
                        let childDirectoryPath = childDirectoryPathsArray[childIndex]
                        let childDirectoryId, childSha = computedDirectoryMetadata[childDirectoryPath]
                        childDirectoryIds.Add(childDirectoryId)

                        localChildDirectories.Add(
                            LocalDirectoryVersion.Create
                                childDirectoryId
                                promotionSetDto.OwnerId
                                promotionSetDto.OrganizationId
                                promotionSetDto.RepositoryId
                                childDirectoryPath
                                childSha
                                (List<DirectoryVersionId>())
                                (List<LocalFileVersion>())
                                0L
                                DateTime.UtcNow
                        )

                        childIndex <- childIndex + 1

                    let directoryFiles = List<FileVersion>()
                    let localDirectoryFiles = List<LocalFileVersion>()
                    let mutable filesForDirectory = Unchecked.defaultof<ResizeArray<FileVersion>>

                    if filesByDirectory.TryGetValue(directoryPath, &filesForDirectory) then
                        let orderedFiles =
                            filesForDirectory
                            |> Seq.sortBy (fun fileVersion -> fileVersion.RelativePath)
                            |> Seq.toArray

                        let mutable orderedFileIndex = 0

                        while orderedFileIndex < orderedFiles.Length do
                            let fileVersion = orderedFiles[orderedFileIndex]
                            directoryFiles.Add(fileVersion)
                            localDirectoryFiles.Add(fileVersion.ToLocalFileVersion DateTime.UtcNow)
                            orderedFileIndex <- orderedFileIndex + 1

                    let computedSha = computeSha256ForDirectory directoryPath localChildDirectories localDirectoryFiles

                    let tryReuseDirectoryId (snapshot: DirectorySnapshot) =
                        let mutable existingDirectoryVersion = DirectoryVersion.Default

                        if
                            snapshot.DirectoriesByPath.TryGetValue(directoryPath, &existingDirectoryVersion)
                            && existingDirectoryVersion.Sha256Hash = computedSha
                        then
                            Option.Some existingDirectoryVersion.DirectoryVersionId
                        else
                            Option.None

                    let reusedDirectoryVersionId =
                        [
                            oursSnapshot
                            theirsSnapshot
                            baseSnapshot
                        ]
                        |> Seq.tryPick tryReuseDirectoryId

                    let directoryVersionId =
                        reusedDirectoryVersionId
                        |> Option.defaultValue (Guid.NewGuid())

                    if reusedDirectoryVersionId.IsNone then
                        let directoryVersion =
                            DirectoryVersion.Create
                                directoryVersionId
                                promotionSetDto.OwnerId
                                promotionSetDto.OrganizationId
                                promotionSetDto.RepositoryId
                                directoryPath
                                computedSha
                                childDirectoryIds
                                directoryFiles
                                (getDirectorySize directoryFiles)

                        directoryVersionsToCreate.Add(directoryVersion)

                    computedDirectoryMetadata[directoryPath] <- (directoryVersionId, computedSha)
                    buildIndex <- buildIndex + 1

                let mutable createIndex = 0

                while createIndex < directoryVersionsToCreate.Count
                      && materializationError.IsNone do
                    let directoryVersion = directoryVersionsToCreate[createIndex]

                    let directoryVersionActorProxy =
                        DirectoryVersion.CreateActorProxy directoryVersion.DirectoryVersionId promotionSetDto.RepositoryId this.correlationId

                    match!
                        directoryVersionActorProxy.Handle
                            (Grace.Types.DirectoryVersion.DirectoryVersionCommand.Create(directoryVersion, repositoryDto))
                            (this.WithActorMetadata metadata)
                        with
                    | Ok _ -> ()
                    | Error graceError -> materializationError <- Option.Some graceError

                    createIndex <- createIndex + 1

                match materializationError with
                | Option.Some graceError -> return Error graceError
                | Option.None ->
                    let mutable rootDirectory = Unchecked.defaultof<(DirectoryVersionId * Sha256Hash)>

                    if computedDirectoryMetadata.TryGetValue(RootDirectoryPath, &rootDirectory) then
                        return Ok(fst rootDirectory)
                    else
                        return
                            Error(
                                (GraceError.Create "Failed to materialize root DirectoryVersion for PromotionSet recompute." metadata.CorrelationId)
                                    .enhance (nameof PromotionSetId, promotionSetDto.PromotionSetId)
                            )
            }

        member private this.ComputeAppliedDirectoryVersionForStep
            (
                step: PromotionSetStep,
                computedAgainstBaseDirectoryVersionId: DirectoryVersionId,
                conflictResolutionPolicy: ConflictResolutionPolicy,
                manualDecisionsForStep: ConflictResolutionDecision list option,
                repositoryDto,
                metadata: EventMetadata
            ) =
            task {
                let! baseSnapshotResult = this.LoadDirectorySnapshot step.OriginalBaseDirectoryVersionId

                match baseSnapshotResult with
                | Error graceError -> return Error(Failed graceError.Error)
                | Ok baseSnapshot ->
                    let! oursSnapshotResult = this.LoadDirectorySnapshot computedAgainstBaseDirectoryVersionId

                    match oursSnapshotResult with
                    | Error graceError -> return Error(Failed graceError.Error)
                    | Ok oursSnapshot ->
                        let! theirsSnapshotResult = this.LoadDirectorySnapshot step.OriginalPromotion.DirectoryVersionId

                        match theirsSnapshotResult with
                        | Error graceError -> return Error(Failed graceError.Error)
                        | Ok theirsSnapshot ->
                            let mergedFilesByPath = Dictionary<RelativePath, FileVersion>(StringComparer.OrdinalIgnoreCase)
                            let conflicts = ResizeArray<StepConflictFile>()
                            let allFilePaths = HashSet<RelativePath>(StringComparer.OrdinalIgnoreCase)

                            baseSnapshot.FilesByPath.Keys
                            |> Seq.iter (fun filePath -> allFilePaths.Add(filePath) |> ignore)

                            oursSnapshot.FilesByPath.Keys
                            |> Seq.iter (fun filePath -> allFilePaths.Add(filePath) |> ignore)

                            theirsSnapshot.FilesByPath.Keys
                            |> Seq.iter (fun filePath -> allFilePaths.Add(filePath) |> ignore)

                            let orderedFilePaths = allFilePaths |> Seq.sortBy id |> Seq.toArray
                            let mutable fileBudgetFailure: RecomputeFailure option = Option.None

                            if orderedFilePaths.Length > maxFilesPerStep then
                                fileBudgetFailure <-
                                    Option.Some(
                                        Failed(
                                            $"Step {step.StepId} exceeded configured file budget ({orderedFilePaths.Length} > {maxFilesPerStep})."
                                        )
                                    )

                            let mutable filePathIndex = 0

                            while filePathIndex < orderedFilePaths.Length
                                  && fileBudgetFailure.IsNone do
                                let filePath = orderedFilePaths[filePathIndex]
                                let baseFile = this.TryGetFileVersion(baseSnapshot.FilesByPath, filePath)
                                let oursFile = this.TryGetFileVersion(oursSnapshot.FilesByPath, filePath)
                                let theirsFile = this.TryGetFileVersion(theirsSnapshot.FilesByPath, filePath)

                                let oursChanged =
                                    not
                                    <| this.FileVersionEquivalent(baseFile, oursFile)

                                let theirsChanged =
                                    not
                                    <| this.FileVersionEquivalent(baseFile, theirsFile)

                                let setMergedFile (fileVersion: FileVersion option) =
                                    match fileVersion with
                                    | Option.Some selected -> mergedFilesByPath[filePath] <- selected
                                    | Option.None -> mergedFilesByPath.Remove(filePath) |> ignore

                                if not theirsChanged then
                                    setMergedFile oursFile
                                elif not oursChanged then
                                    setMergedFile theirsFile
                                elif this.FileVersionEquivalent(oursFile, theirsFile) then
                                    setMergedFile oursFile
                                else
                                    let isBinary =
                                        match oursFile, theirsFile with
                                        | Option.Some ours, _
                                        | _, Option.Some ours -> ours.IsBinary
                                        | _ -> false

                                    conflicts.Add(
                                        { FilePath = filePath; BaseFile = baseFile; OursFile = oursFile; TheirsFile = theirsFile; IsBinary = isBinary }
                                    )

                                filePathIndex <- filePathIndex + 1

                            match fileBudgetFailure with
                            | Option.Some recomputeFailure -> return Error recomputeFailure
                            | Option.None when conflicts.Count = 0 ->
                                match!
                                    this.MaterializeMergedDirectoryVersion
                                        (
                                            repositoryDto,
                                            baseSnapshot,
                                            oursSnapshot,
                                            theirsSnapshot,
                                            mergedFilesByPath,
                                            metadata
                                        )
                                    with
                                | Ok appliedDirectoryVersionId ->
                                    return Ok(appliedDirectoryVersionId, StepConflictStatus.NoConflicts, Option.None)
                                | Error graceError -> return Error(Failed graceError.Error)
                            | Option.None ->
                                let acceptedDecisionsByPath = Dictionary<RelativePath, ConflictResolutionDecision>(StringComparer.OrdinalIgnoreCase)
                                let acceptedFilePaths = HashSet<RelativePath>(StringComparer.OrdinalIgnoreCase)
                                let decisions = manualDecisionsForStep |> Option.defaultValue []
                                let mutable decisionIndex = 0

                                while decisionIndex < decisions.Length do
                                    let decision = decisions[decisionIndex]

                                    if decision.Accepted then
                                        acceptedDecisionsByPath[normalizeFilePath decision.FilePath] <- decision

                                    decisionIndex <- decisionIndex + 1

                                let unresolvedConflicts = ResizeArray<StepConflictFile>()
                                let mutable resolutionError: GraceError option = Option.None
                                let mutable conflictIndex = 0

                                while conflictIndex < conflicts.Count
                                      && resolutionError.IsNone do
                                    let conflictFile = conflicts[conflictIndex]
                                    let normalizedFilePath = normalizeFilePath conflictFile.FilePath
                                    let mutable acceptedDecision = Unchecked.defaultof<ConflictResolutionDecision>

                                    if acceptedDecisionsByPath.TryGetValue(normalizedFilePath, &acceptedDecision) then
                                        acceptedFilePaths.Add(conflictFile.FilePath)
                                        |> ignore

                                        match acceptedDecision.OverrideContentArtifactId with
                                        | Option.Some artifactId ->
                                            let! artifactTextResult = this.GetArtifactText(artifactId, metadata)

                                            match artifactTextResult with
                                            | Error graceError -> resolutionError <- Option.Some graceError
                                            | Ok artifactText ->
                                                let! overrideFileVersionResult =
                                                    this.CreateTextOverrideFileVersion(conflictFile.FilePath, artifactText, repositoryDto, metadata)

                                                match overrideFileVersionResult with
                                                | Error graceError -> resolutionError <- Option.Some graceError
                                                | Ok overrideFileVersion -> mergedFilesByPath[conflictFile.FilePath] <- overrideFileVersion
                                        | Option.None ->
                                            match conflictFile.TheirsFile with
                                            | Option.Some theirsFile -> mergedFilesByPath[conflictFile.FilePath] <- theirsFile
                                            | Option.None ->
                                                mergedFilesByPath.Remove(conflictFile.FilePath)
                                                |> ignore
                                    else
                                        unresolvedConflicts.Add(conflictFile)

                                    conflictIndex <- conflictIndex + 1

                                match resolutionError with
                                | Option.Some graceError -> return Error(Failed graceError.Error)
                                | Option.None ->
                                    let hasUnresolvedConflicts = unresolvedConflicts.Count > 0

                                    let hasUnresolvedBinaryConflict =
                                        unresolvedConflicts
                                        |> Seq.exists (fun conflict -> conflict.IsBinary)

                                    let mutable confidence: float option = Option.None
                                    let mutable blockedReason: string option = Option.None
                                    let mutable appliedByPolicy = false

                                    if hasUnresolvedConflicts then
                                        match conflictResolutionPolicy with
                                        | ConflictResolutionPolicy.NoConflicts _ ->
                                            blockedReason <- Option.Some $"Conflict detected at step {step.StepId}, and repository policy is NoConflicts."
                                        | ConflictResolutionPolicy.ConflictsAllowed threshold ->
                                            if hasUnresolvedBinaryConflict then
                                                blockedReason <- Option.Some "Binary file conflicts require manual override and cannot be auto-merged."
                                            else
                                                let modelConfidence =
                                                    this.GetModelConfidence(step, computedAgainstBaseDirectoryVersionId, unresolvedConflicts.Count)

                                                confidence <- Option.Some modelConfidence

                                                if modelConfidence >= float threshold then
                                                    appliedByPolicy <- true

                                                    let mutable unresolvedIndex = 0

                                                    while unresolvedIndex < unresolvedConflicts.Count do
                                                        let unresolvedConflict = unresolvedConflicts[unresolvedIndex]

                                                        match unresolvedConflict.TheirsFile with
                                                        | Option.Some theirsFile -> mergedFilesByPath[unresolvedConflict.FilePath] <- theirsFile
                                                        | Option.None ->
                                                            mergedFilesByPath.Remove(unresolvedConflict.FilePath)
                                                            |> ignore

                                                        unresolvedIndex <- unresolvedIndex + 1
                                                else
                                                    blockedReason <-
                                                        Option.Some(
                                                            sprintf
                                                                "Conflict confidence %.2f is below threshold %.2f at step %O."
                                                                modelConfidence
                                                                (float threshold)
                                                                step.StepId
                                                        )

                                    let conflictFilesForArtifact = conflicts |> Seq.toList

                                    let resolutionMethod =
                                        if appliedByPolicy then ConflictResolutionMethod.ModelSuggested
                                        elif acceptedFilePaths.Count > 0 then ConflictResolutionMethod.ManualOverride
                                        elif confidence.IsSome then ConflictResolutionMethod.ModelSuggested
                                        else ConflictResolutionMethod.None

                                    let! conflictAnalysesResult =
                                        this.BuildConflictAnalyses(repositoryDto, conflictFilesForArtifact, confidence, resolutionMethod, acceptedFilePaths)

                                    match conflictAnalysesResult with
                                    | Error graceError -> return Error(Failed graceError.Error)
                                    | Ok conflictAnalyses ->
                                        match blockedReason with
                                        | Option.Some reasonText ->
                                            let! artifactId =
                                                this.CreateConflictArtifact(
                                                    step,
                                                    reasonText,
                                                    confidence,
                                                    computedAgainstBaseDirectoryVersionId,
                                                    metadata,
                                                    manualDecisionsForStep,
                                                    Option.Some conflictAnalyses
                                                )

                                            return Error(Blocked(reasonText, artifactId))
                                        | Option.None ->
                                            let resolutionReason =
                                                if appliedByPolicy && confidence.IsSome then
                                                    sprintf "Conflicts auto-resolved by policy at confidence %.2f." confidence.Value
                                                elif acceptedFilePaths.Count > 0 then
                                                    "Conflicts resolved through manual review decisions."
                                                else
                                                    "Conflicts resolved."

                                            let! conflictArtifactId =
                                                this.CreateConflictArtifact(
                                                    step,
                                                    resolutionReason,
                                                    confidence,
                                                    computedAgainstBaseDirectoryVersionId,
                                                    metadata,
                                                    manualDecisionsForStep,
                                                    Option.Some conflictAnalyses
                                                )

                                            match!
                                                this.MaterializeMergedDirectoryVersion
                                                    (
                                                        repositoryDto,
                                                        baseSnapshot,
                                                        oursSnapshot,
                                                        theirsSnapshot,
                                                        mergedFilesByPath,
                                                        metadata
                                                    )
                                                with
                                            | Ok appliedDirectoryVersionId ->
                                                return Ok(appliedDirectoryVersionId, StepConflictStatus.AutoResolved, conflictArtifactId)
                                            | Error graceError -> return Error(Failed graceError.Error)
            }

        member private this.CreateConflictArtifact
            (
                step: PromotionSetStep,
                reason: string,
                confidence: float option,
                computedAgainstBaseDirectoryVersionId: DirectoryVersionId,
                metadata: EventMetadata,
                manualDecisions: ConflictResolutionDecision list option,
                conflictAnalyses: ConflictAnalysis list option
            ) =
            task {
                try
                    let defaultConflictAnalysis: ConflictAnalysis =
                        {
                            FilePath = "__step__"
                            OriginalHunks =
                                [
                                    {
                                        StartLine = 1
                                        EndLine = 1
                                        OursContent = $"{computedAgainstBaseDirectoryVersionId}"
                                        TheirsContent = $"{step.OriginalPromotion.DirectoryVersionId}"
                                    }
                                ]
                            ProposedResolution = Option.None
                            ResolutionMethod = ConflictResolutionMethod.None
                        }

                    let report =
                        {|
                            promotionSetId = promotionSetDto.PromotionSetId
                            targetBranchId = promotionSetDto.TargetBranchId
                            stepId = step.StepId
                            order = step.Order
                            reason = reason
                            confidence = confidence
                            computedAgainstBaseDirectoryVersionId = computedAgainstBaseDirectoryVersionId
                            originalBaseDirectoryVersionId = step.OriginalBaseDirectoryVersionId
                            originalPromotionDirectoryVersionId = step.OriginalPromotion.DirectoryVersionId
                            conflicts =
                                conflictAnalyses
                                |> Option.defaultValue [ defaultConflictAnalysis ]
                            manualDecisions =
                                (manualDecisions
                                 |> Option.defaultValue []
                                 |> List.map (fun decision ->
                                     {|
                                         filePath = decision.FilePath
                                         accepted = decision.Accepted
                                         overrideContentArtifactId = decision.OverrideContentArtifactId
                                     |}))
                        |}

                    let reportJson = serialize report
                    let artifactId: ArtifactId = Guid.NewGuid()
                    let nowUtc = getCurrentInstant().InUtc()

                    let blobPath = sprintf "grace-artifacts/%04i/%02i/%02i/%02i/%O" nowUtc.Year nowUtc.Month nowUtc.Day nowUtc.Hour artifactId

                    let artifactMetadata: ArtifactMetadata =
                        { ArtifactMetadata.Default with
                            ArtifactId = artifactId
                            OwnerId = promotionSetDto.OwnerId
                            OrganizationId = promotionSetDto.OrganizationId
                            RepositoryId = promotionSetDto.RepositoryId
                            ArtifactType = ArtifactType.ConflictReport
                            MimeType = "application/json"
                            Size = int64 reportJson.Length
                            BlobPath = blobPath
                            CreatedAt = getCurrentInstant ()
                            CreatedBy = UserId metadata.Principal
                        }

                    let artifactActorProxy = Artifact.CreateActorProxy artifactId promotionSetDto.RepositoryId this.correlationId

                    match! artifactActorProxy.Handle (ArtifactCommand.Create artifactMetadata) (this.WithActorMetadata metadata) with
                    | Ok _ ->
                        match! this.UploadArtifactPayload(blobPath, reportJson, metadata) with
                        | Ok () -> return Option.Some artifactId
                        | Error uploadError ->
                            log.LogWarning(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Conflict artifact metadata was created but payload upload failed for PromotionSetId {PromotionSetId}; ArtifactId {ArtifactId}. Error: {GraceError}.",
                                getCurrentInstantExtended (),
                                getMachineName,
                                metadata.CorrelationId,
                                promotionSetDto.PromotionSetId,
                                artifactId,
                                uploadError
                            )

                            return Option.Some artifactId
                    | Error graceError ->
                        log.LogWarning(
                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed to persist conflict artifact metadata for PromotionSetId {PromotionSetId}. Error: {GraceError}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            metadata.CorrelationId,
                            promotionSetDto.PromotionSetId,
                            graceError
                        )

                        return Option.None
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Exception while creating conflict artifact for PromotionSetId {PromotionSetId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        metadata.CorrelationId,
                        promotionSetDto.PromotionSetId
                    )

                    return Option.None
            }

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            promotionSetDto <-
                state.State
                |> Seq.fold (fun dto event -> PromotionSetDto.UpdateDto event dto) promotionSetDto

            Task.CompletedTask

        interface IGraceReminderWithGuidKey with
            member this.ScheduleReminderAsync reminderType delay reminderState correlationId =
                task {
                    let reminderDto =
                        ReminderDto.Create
                            actorName
                            $"{this.IdentityString}"
                            promotionSetDto.OwnerId
                            promotionSetDto.OrganizationId
                            promotionSetDto.RepositoryId
                            reminderType
                            (getFutureInstant delay)
                            reminderState
                            correlationId

                    do! createReminder reminderDto
                }
                :> Task

            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                task {
                    this.correlationId <- reminder.CorrelationId

                    return
                        Error(
                            (GraceError.Create
                                $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminder.ReminderType} with state {getDiscriminatedUnionCaseName reminder.State}."
                                this.correlationId)
                                .enhance ("IsRetryable", "false")
                        )
                }

        member private this.ApplyEvent(promotionSetEvent: PromotionSetEvent) =
            task {
                let normalizedMetadata = this.WithActorMetadata promotionSetEvent.Metadata
                let normalizedEvent = { promotionSetEvent with Metadata = normalizedMetadata }
                let correlationId = normalizedMetadata.CorrelationId

                try
                    state.State.Add(normalizedEvent)
                    do! state.WriteStateAsync()

                    promotionSetDto <-
                        promotionSetDto
                        |> PromotionSetDto.UpdateDto normalizedEvent

                    let graceEvent = GraceEvent.PromotionSetEvent normalizedEvent
                    do! publishGraceEvent graceEvent normalizedMetadata
                    return this.BuildSuccess("Promotion set command succeeded.", correlationId)
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed to apply event for PromotionSetId: {PromotionSetId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        promotionSetDto.PromotionSetId
                    )

                    return
                        Error(
                            (GraceError.CreateWithException ex "Failed while applying PromotionSet event." correlationId)
                                .enhance(nameof RepositoryId, promotionSetDto.RepositoryId)
                                .enhance (nameof PromotionSetId, promotionSetDto.PromotionSetId)
                        )
            }

        member private this.RecomputeSteps
            (
                metadata: EventMetadata,
                reason: string option,
                manualResolution: (PromotionSetStepId * ConflictResolutionDecision list) option
            ) =
            task {
                if promotionSetDto.StepsComputationStatus = StepsComputationStatus.Computing then
                    return this.BuildError("PromotionSet steps are already computing.", metadata.CorrelationId)
                else
                    let! currentTerminalReferenceId, targetBaseDirectoryVersionId = this.GetCurrentTerminalPromotion()

                    if manualResolution.IsNone
                       && promotionSetDto.StepsComputationStatus = StepsComputationStatus.Computed
                       && promotionSetDto.ComputedAgainstParentTerminalPromotionReferenceId = Option.Some currentTerminalReferenceId then
                        return this.BuildSuccess("PromotionSet steps are already computed against the current terminal promotion.", metadata.CorrelationId)
                    else
                        match! this.ApplyEvent { Event = PromotionSetEventType.RecomputeStarted currentTerminalReferenceId; Metadata = metadata } with
                        | Error graceError -> return Error graceError
                        | Ok _ ->
                            let! conflictResolutionPolicy = this.GetConflictResolutionPolicy()
                            let! repositoryDto = this.GetRepositoryDto()

                            let orderedSteps =
                                promotionSetDto.Steps
                                |> List.sortBy (fun step -> step.Order)

                            let computedSteps = ResizeArray<PromotionSetStep>()
                            let mutable recomputeFailure: RecomputeFailure option = Option.None
                            let mutable currentHeadDirectoryVersionId = targetBaseDirectoryVersionId
                            let mutable index = 0
                            let recomputeStopwatch = Stopwatch.StartNew()

                            if orderedSteps.Length > maxStepsPerRecompute then
                                recomputeFailure <-
                                    Option.Some(Failed($"Recompute exceeded configured step budget ({orderedSteps.Length} > {maxStepsPerRecompute})."))

                            while index < orderedSteps.Length
                                  && recomputeFailure.IsNone do
                                if recomputeStopwatch.ElapsedMilliseconds > int64 maxTotalTimeMilliseconds then
                                    recomputeFailure <-
                                        Option.Some(
                                            Failed(
                                                $"Recompute exceeded total time budget ({recomputeStopwatch.ElapsedMilliseconds} ms > {maxTotalTimeMilliseconds} ms)."
                                            )
                                        )

                                let currentStep = orderedSteps[index]
                                let stepStopwatch = Stopwatch.StartNew()
                                let! hydratedStepResult = this.HydrateStepProvenance currentStep

                                match hydratedStepResult with
                                | Error graceError -> recomputeFailure <- Option.Some(Failed graceError.Error)
                                | Ok hydratedStep ->
                                    let computedAgainstBaseDirectoryVersionId = currentHeadDirectoryVersionId

                                    let manualDecisionsForStep =
                                        match manualResolution with
                                        | Option.Some (resolvedStepId, decisions) when resolvedStepId = hydratedStep.StepId -> Option.Some decisions
                                        | _ -> Option.None

                                    let hasManualOverride =
                                        manualDecisionsForStep
                                        |> Option.defaultValue []
                                        |> List.exists (fun decision ->
                                            decision.Accepted
                                            && decision.OverrideContentArtifactId.IsSome)

                                    let! manualOverrideValidation =
                                        if hasManualOverride then
                                            this.ValidateManualOverrideArtifacts(manualDecisionsForStep |> Option.defaultValue [], metadata)
                                        else
                                            Task.FromResult(Ok())

                                    match manualOverrideValidation with
                                    | Error graceError -> recomputeFailure <- Option.Some(Failed graceError.Error)
                                    | Ok () ->
                                        let! stepComputationResult =
                                            this.ComputeAppliedDirectoryVersionForStep(
                                                hydratedStep,
                                                computedAgainstBaseDirectoryVersionId,
                                                conflictResolutionPolicy,
                                                manualDecisionsForStep,
                                                repositoryDto,
                                                metadata
                                            )

                                        match stepComputationResult with
                                        | Ok (appliedDirectoryVersionId, conflictStatus, conflictArtifactId) ->
                                            let computedStep =
                                                { hydratedStep with
                                                    ComputedAgainstBaseDirectoryVersionId = computedAgainstBaseDirectoryVersionId
                                                    AppliedDirectoryVersionId = appliedDirectoryVersionId
                                                    ConflictSummaryArtifactId = conflictArtifactId
                                                    ConflictStatus = conflictStatus
                                                }

                                            computedSteps.Add(computedStep)
                                            currentHeadDirectoryVersionId <- computedStep.AppliedDirectoryVersionId
                                        | Error stepFailure -> recomputeFailure <- Option.Some stepFailure

                                if recomputeFailure.IsNone
                                   && stepStopwatch.ElapsedMilliseconds > int64 maxStepTimeMilliseconds then
                                    recomputeFailure <-
                                        Option.Some(
                                            Failed(
                                                $"Step {currentStep.StepId} exceeded step time budget ({stepStopwatch.ElapsedMilliseconds} ms > {maxStepTimeMilliseconds} ms)."
                                            )
                                        )

                                index <- index + 1

                            match recomputeFailure with
                            | Option.None ->
                                match!
                                    this.ApplyEvent
                                        {
                                            Event = PromotionSetEventType.StepsUpdated(computedSteps |> Seq.toList, currentTerminalReferenceId)
                                            Metadata = metadata
                                        }
                                    with
                                | Ok graceReturnValue -> return Ok graceReturnValue
                                | Error graceError -> return Error graceError
                            | Option.Some (Blocked (reasonText, artifactId)) ->
                                match! this.ApplyEvent { Event = PromotionSetEventType.Blocked(reasonText, artifactId); Metadata = metadata } with
                                | Ok _ -> return this.BuildError(reasonText, metadata.CorrelationId)
                                | Error graceError -> return Error graceError
                            | Option.Some (Failed reasonText) ->
                                match!
                                    this.ApplyEvent
                                        { Event = PromotionSetEventType.RecomputeFailed(reasonText, currentTerminalReferenceId); Metadata = metadata }
                                    with
                                | Ok _ -> return this.BuildError(reasonText, metadata.CorrelationId)
                                | Error graceError -> return Error graceError
            }

        member private this.RollbackCreatedPromotions(createdReferenceIds: List<ReferenceId>, rollbackReason: string, metadata: EventMetadata) =
            task {
                let mutable index = 0

                while index < createdReferenceIds.Count do
                    let referenceId = createdReferenceIds[index]
                    let referenceActorProxy = Reference.CreateActorProxy referenceId promotionSetDto.RepositoryId this.correlationId
                    let rollbackMetadata = this.WithActorMetadata metadata
                    rollbackMetadata.Properties[ "ActorId" ] <- $"{referenceId}"

                    match! referenceActorProxy.Handle (ReferenceCommand.DeleteLogical(true, rollbackReason)) rollbackMetadata with
                    | Ok _ -> ()
                    | Error graceError ->
                        log.LogWarning(
                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed to rollback reference {ReferenceId} for PromotionSetId {PromotionSetId}. Error: {GraceError}",
                            getCurrentInstantExtended (),
                            getMachineName,
                            metadata.CorrelationId,
                            referenceId,
                            promotionSetDto.PromotionSetId,
                            graceError
                        )

                    index <- index + 1

                let branchActorProxy = Branch.CreateActorProxy promotionSetDto.TargetBranchId promotionSetDto.RepositoryId this.correlationId
                do! branchActorProxy.MarkForRecompute metadata.CorrelationId
            }

        member private this.CreatePromotionReference(step: PromotionSetStep, isTerminal: bool, metadata: EventMetadata) =
            task {
                let directoryVersionActorProxy =
                    DirectoryVersion.CreateActorProxy step.AppliedDirectoryVersionId promotionSetDto.RepositoryId this.correlationId

                let! directoryVersionDto = directoryVersionActorProxy.Get this.correlationId

                if directoryVersionDto.DirectoryVersion.DirectoryVersionId = DirectoryVersionId.Empty then
                    return
                        Error(
                            (GraceError.Create "Applied directory version does not exist." metadata.CorrelationId)
                                .enhance(nameof PromotionSetStepId, step.StepId)
                                .enhance (nameof DirectoryVersionId, step.AppliedDirectoryVersionId)
                        )
                else
                    let referenceId: ReferenceId = Guid.NewGuid()
                    let links = ResizeArray<ReferenceLinkType>()
                    links.Add(ReferenceLinkType.IncludedInPromotionSet promotionSetDto.PromotionSetId)

                    if isTerminal then
                        links.Add(ReferenceLinkType.PromotionSetTerminal promotionSetDto.PromotionSetId)

                    let referenceMetadata = this.WithActorMetadata metadata
                    referenceMetadata.Properties[ "ActorId" ] <- $"{referenceId}"
                    referenceMetadata.Properties[ nameof BranchId ] <- $"{promotionSetDto.TargetBranchId}"
                    let referenceActorProxy = Reference.CreateActorProxy referenceId promotionSetDto.RepositoryId this.correlationId
                    let referenceText = ReferenceText $"PromotionSet {promotionSetDto.PromotionSetId} Step {step.Order}"

                    let referenceCommand =
                        ReferenceCommand.Create(
                            referenceId,
                            promotionSetDto.OwnerId,
                            promotionSetDto.OrganizationId,
                            promotionSetDto.RepositoryId,
                            promotionSetDto.TargetBranchId,
                            step.AppliedDirectoryVersionId,
                            directoryVersionDto.DirectoryVersion.Sha256Hash,
                            ReferenceType.Promotion,
                            referenceText,
                            links
                        )

                    match! referenceActorProxy.Handle referenceCommand referenceMetadata with
                    | Ok _ -> return Ok referenceId
                    | Error graceError -> return Error graceError
            }

        member private this.GetRequiredValidationsForApply() =
            task {
                let! validationSets = getValidationSets promotionSetDto.RepositoryId 500 false this.correlationId

                return
                    validationSets
                    |> List.filter (fun validationSet -> validationSet.TargetBranchId = promotionSetDto.TargetBranchId)
                    |> List.collect (fun validationSet -> validationSet.Validations)
                    |> List.filter (fun validation -> validation.RequiredForApply)
                    |> List.distinctBy (fun validation -> $"{validation.Name.Trim().ToLowerInvariant()}::{validation.Version.Trim().ToLowerInvariant()}")
            }

        member private this.EnsureRequiredValidationsPass(metadata: EventMetadata) =
            task {
                let! requiredValidations = this.GetRequiredValidationsForApply()

                if requiredValidations.IsEmpty then
                    return Ok()
                else
                    let! scopedValidationResults =
                        getValidationResultsForPromotionSetAttempt
                            promotionSetDto.RepositoryId
                            promotionSetDto.PromotionSetId
                            promotionSetDto.StepsComputationAttempt
                            5000
                            this.correlationId

                    let hasPass (validationName: string) (validationVersion: string) =
                        scopedValidationResults
                        |> List.exists (fun validationResult ->
                            String.Equals(validationResult.ValidationName, validationName, StringComparison.OrdinalIgnoreCase)
                            && String.Equals(validationResult.ValidationVersion, validationVersion, StringComparison.OrdinalIgnoreCase)
                            && validationResult.Output.Status = ValidationStatus.Pass)

                    let missingOrFailing =
                        requiredValidations
                        |> List.filter (fun validation -> not <| hasPass validation.Name validation.Version)

                    if missingOrFailing.IsEmpty then
                        return Ok()
                    else
                        let details =
                            missingOrFailing
                            |> List.map (fun validation -> $"{validation.Name}:{validation.Version}")
                            |> String.concat ", "

                        return
                            Error(
                                (GraceError.Create
                                    $"Required validations have not passed for StepsComputationAttempt {promotionSetDto.StepsComputationAttempt}: {details}."
                                    metadata.CorrelationId)
                                    .enhance(nameof PromotionSetId, promotionSetDto.PromotionSetId)
                                    .enhance ("StepsComputationAttempt", promotionSetDto.StepsComputationAttempt)
                            )
            }

        member private this.ApplyPromotionSet(metadata: EventMetadata) =
            task {
                if promotionSetDto.Status = PromotionSetStatus.Succeeded then
                    return this.BuildError("PromotionSet has already been applied successfully.", metadata.CorrelationId)
                elif promotionSetDto.DeletedAt.IsSome then
                    return this.BuildError("PromotionSet has been deleted and cannot be applied.", metadata.CorrelationId)
                else
                    let! currentTerminalReferenceId, _ = this.GetCurrentTerminalPromotion()

                    let needsRecompute =
                        promotionSetDto.StepsComputationStatus
                        <> StepsComputationStatus.Computed
                        || promotionSetDto.ComputedAgainstParentTerminalPromotionReferenceId
                           <> Option.Some currentTerminalReferenceId

                    let mutable recomputeError: GraceError option = Option.None

                    if needsRecompute then
                        let! recomputeResult = this.RecomputeSteps(metadata, Option.Some "Apply requested on stale or uncomputed steps.", Option.None)

                        match recomputeResult with
                        | Ok _ -> ()
                        | Error graceError -> recomputeError <- Option.Some graceError

                    let mutable preconditionError: GraceError option = recomputeError

                    if preconditionError.IsNone
                       && promotionSetDto.StepsComputationStatus
                          <> StepsComputationStatus.Computed then
                        preconditionError <- Option.Some(GraceError.Create "PromotionSet steps are not computed." metadata.CorrelationId)

                    if preconditionError.IsNone
                       && promotionSetDto.Steps.IsEmpty then
                        preconditionError <- Option.Some(GraceError.Create "PromotionSet does not have any steps to apply." metadata.CorrelationId)

                    if preconditionError.IsNone then
                        match! this.EnsureRequiredValidationsPass metadata with
                        | Ok _ -> ()
                        | Error graceError -> preconditionError <- Option.Some graceError

                    if preconditionError.IsSome then
                        return Error preconditionError.Value
                    else
                        match! this.ApplyEvent { Event = PromotionSetEventType.ApplyStarted; Metadata = metadata } with
                        | Error graceError -> return Error graceError
                        | Ok _ ->
                            let createdReferenceIds = List<ReferenceId>()

                            let orderedSteps =
                                promotionSetDto.Steps
                                |> List.sortBy (fun step -> step.Order)

                            let mutable applyError: GraceError option = Option.None
                            let mutable index = 0

                            while index < orderedSteps.Length && applyError.IsNone do
                                let step = orderedSteps[index]
                                let isTerminal = index = (orderedSteps.Length - 1)
                                let! createReferenceResult = this.CreatePromotionReference(step, isTerminal, metadata)

                                match createReferenceResult with
                                | Ok referenceId -> createdReferenceIds.Add(referenceId)
                                | Error graceError -> applyError <- Option.Some graceError

                                index <- index + 1

                            match applyError with
                            | Option.None ->
                                let branchActorProxy = Branch.CreateActorProxy promotionSetDto.TargetBranchId promotionSetDto.RepositoryId this.correlationId
                                do! branchActorProxy.MarkForRecompute metadata.CorrelationId
                                let terminalReferenceId = createdReferenceIds[createdReferenceIds.Count - 1]

                                match! this.ApplyEvent { Event = PromotionSetEventType.Applied terminalReferenceId; Metadata = metadata } with
                                | Error graceError -> return Error graceError
                                | Ok graceReturnValue ->
                                    let queueActorProxy =
                                        PromotionQueue.CreateActorProxy promotionSetDto.TargetBranchId promotionSetDto.RepositoryId this.correlationId

                                    let! queueExists = queueActorProxy.Exists metadata.CorrelationId

                                    if queueExists then
                                        let dequeueMetadata = this.WithActorMetadata metadata

                                        match! queueActorProxy.Handle (PromotionQueueCommand.Dequeue promotionSetDto.PromotionSetId) dequeueMetadata with
                                        | Ok _ -> ()
                                        | Error graceError ->
                                            log.LogWarning(
                                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed to dequeue PromotionSetId {PromotionSetId} after apply. Error: {GraceError}",
                                                getCurrentInstantExtended (),
                                                getMachineName,
                                                metadata.CorrelationId,
                                                promotionSetDto.PromotionSetId,
                                                graceError
                                            )

                                    return Ok graceReturnValue
                            | Option.Some graceError ->
                                do!
                                    this.RollbackCreatedPromotions(
                                        createdReferenceIds,
                                        "PromotionSet apply failed. Rolling back previously created references.",
                                        metadata
                                    )

                                match! this.ApplyEvent { Event = PromotionSetEventType.ApplyFailed graceError.Error; Metadata = metadata } with
                                | Ok _ -> return Error graceError
                                | Error applyFailureError -> return Error applyFailureError
            }

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId = promotionSetDto.RepositoryId |> returnTask

        interface IPromotionSetActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                not
                <| promotionSetDto.PromotionSetId.Equals(PromotionSetId.Empty)
                |> returnTask

            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                promotionSetDto.DeletedAt.IsSome |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                promotionSetDto |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                state.State :> IReadOnlyList<PromotionSetEvent>
                |> returnTask

            member this.Handle command metadata =
                let isValid (promotionSetCommand: PromotionSetCommand) (eventMetadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = eventMetadata.CorrelationId) then
                            return Error(GraceError.Create "Duplicate correlation ID for PromotionSet command." eventMetadata.CorrelationId)
                        else
                            match promotionSetCommand with
                            | PromotionSetCommand.CreatePromotionSet _ when
                                promotionSetDto.PromotionSetId
                                <> PromotionSetId.Empty
                                ->
                                return Error(GraceError.Create "PromotionSet already exists." eventMetadata.CorrelationId)
                            | PromotionSetCommand.CreatePromotionSet _ -> return Ok promotionSetCommand
                            | _ when promotionSetDto.PromotionSetId = PromotionSetId.Empty ->
                                return Error(GraceError.Create "PromotionSet does not exist." eventMetadata.CorrelationId)
                            | PromotionSetCommand.Apply when promotionSetDto.Status = PromotionSetStatus.Succeeded ->
                                return Error(GraceError.Create "PromotionSet has already been applied successfully." eventMetadata.CorrelationId)
                            | PromotionSetCommand.Apply when promotionSetDto.Status = PromotionSetStatus.Running ->
                                return Error(GraceError.Create "PromotionSet is already running." eventMetadata.CorrelationId)
                            | PromotionSetCommand.RecomputeStepsIfStale _ when promotionSetDto.StepsComputationStatus = StepsComputationStatus.Computing ->
                                return Error(GraceError.Create "PromotionSet steps are already computing." eventMetadata.CorrelationId)
                            | PromotionSetCommand.ResolveConflicts _ when
                                promotionSetDto.Status
                                <> PromotionSetStatus.Blocked
                                ->
                                return Error(GraceError.Create "PromotionSet is not blocked for conflict review." eventMetadata.CorrelationId)
                            | PromotionSetCommand.UpdateInputPromotions _ when promotionSetDto.Status = PromotionSetStatus.Succeeded ->
                                return Error(GraceError.Create "PromotionSet has already succeeded and cannot be edited." eventMetadata.CorrelationId)
                            | _ -> return Ok promotionSetCommand
                    }

                let processCommand (promotionSetCommand: PromotionSetCommand) (eventMetadata: EventMetadata) =
                    task {
                        match promotionSetCommand with
                        | PromotionSetCommand.CreatePromotionSet (promotionSetId, ownerId, organizationId, repositoryId, targetBranchId) ->
                            return!
                                this.ApplyEvent
                                    {
                                        Event = PromotionSetEventType.Created(promotionSetId, ownerId, organizationId, repositoryId, targetBranchId)
                                        Metadata = eventMetadata
                                    }
                        | PromotionSetCommand.UpdateInputPromotions promotionPointers ->
                            match! this.ApplyEvent { Event = PromotionSetEventType.InputPromotionsUpdated promotionPointers; Metadata = eventMetadata } with
                            | Error graceError -> return Error graceError
                            | Ok _ -> return! this.RecomputeSteps(eventMetadata, Option.Some "Input promotions changed.", Option.None)
                        | PromotionSetCommand.RecomputeStepsIfStale reason -> return! this.RecomputeSteps(eventMetadata, reason, Option.None)
                        | PromotionSetCommand.ResolveConflicts (stepId, resolutions) ->
                            return! this.RecomputeSteps(eventMetadata, Option.Some "Manual conflict resolutions submitted.", Option.Some(stepId, resolutions))
                        | PromotionSetCommand.Apply -> return! this.ApplyPromotionSet eventMetadata
                        | PromotionSetCommand.DeleteLogical (force, deleteReason) ->
                            return! this.ApplyEvent { Event = PromotionSetEventType.LogicalDeleted(force, deleteReason); Metadata = eventMetadata }
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, getDiscriminatedUnionCaseName command)

                    match! isValid command metadata with
                    | Ok validCommand -> return! processCommand validCommand metadata
                    | Error validationError -> return Error validationError
                }
