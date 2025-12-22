namespace Grace.Server.Services

open Grace.Actors.Extensions.ActorProxy
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Services
open Grace.Shared.Utilities
open Grace.Types.DirectoryVersion
open Grace.Types.PatchSet
open Grace.Types.Types
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Linq

module ApplyPatchSet =

    type ApplyResult =
        { DirectoryVersions: DirectoryVersionDto array
          RootDirectoryId: DirectoryVersionId
          Conflicts: PatchFile array }

    let private ensureDirectory
        (directories: Dictionary<RelativePath, LocalDirectoryVersion>)
        (ownerId: OwnerId)
        (organizationId: OrganizationId)
        (repositoryId: RepositoryId)
        (relativePath: RelativePath)
        =
        if not (directories.ContainsKey(relativePath)) then
            let directoryVersionId = DirectoryVersionId.NewGuid()
            let directory =
                LocalDirectoryVersion.Create
                    directoryVersionId
                    ownerId
                    organizationId
                    repositoryId
                    relativePath
                    (Sha256Hash String.Empty)
                    (List<DirectoryVersionId>())
                    (List<LocalFileVersion>())
                    0L
                    DateTime.UtcNow
            directories[relativePath] <- directory

        directories[relativePath]

    let private selectFileVersion (patchFile: PatchFile) (baseMap: Dictionary<RelativePath, FileVersion>) (headMap: Dictionary<RelativePath, FileVersion>) =
        match patchFile.Operation with
        | PatchOperation.AddFile ->
            if headMap.ContainsKey(patchFile.RelativePath) then
                Some headMap[patchFile.RelativePath]
            else
                None
        | PatchOperation.ModifyFile ->
            if headMap.ContainsKey(patchFile.RelativePath) then
                Some headMap[patchFile.RelativePath]
            else
                None
        | PatchOperation.DeleteFile -> None
        | PatchOperation.RenameFile(_, newPath) ->
            if headMap.ContainsKey(newPath) then
                Some headMap[newPath]
            else
                None

    let private computeConflicts
        (patchSet: PatchSetDto)
        (baseMap: Dictionary<RelativePath, FileVersion>)
        (newBaseMap: Dictionary<RelativePath, FileVersion>)
        =
        patchSet.Files
        |> Array.filter (fun patchFile ->
            match patchFile.Operation with
            | PatchOperation.AddFile -> newBaseMap.ContainsKey(patchFile.RelativePath)
            | PatchOperation.DeleteFile ->
                newBaseMap.ContainsKey(patchFile.RelativePath)
                && (not <| baseMap.ContainsKey(patchFile.RelativePath)
                    || newBaseMap[patchFile.RelativePath].Sha256Hash <> baseMap[patchFile.RelativePath].Sha256Hash)
            | PatchOperation.ModifyFile ->
                newBaseMap.ContainsKey(patchFile.RelativePath)
                && baseMap.ContainsKey(patchFile.RelativePath)
                && newBaseMap[patchFile.RelativePath].Sha256Hash <> baseMap[patchFile.RelativePath].Sha256Hash
            | PatchOperation.RenameFile(oldPath, newPath) ->
                (newBaseMap.ContainsKey(oldPath) && baseMap.ContainsKey(oldPath) && newBaseMap[oldPath].Sha256Hash <> baseMap[oldPath].Sha256Hash)
                || newBaseMap.ContainsKey(newPath))

    let applyPatchSet
        (repositoryId: RepositoryId)
        (patchSet: PatchSetDto)
        (newBaseDirectoryId: DirectoryVersionId)
        (correlationId: CorrelationId)
        =
        task {
            let! baseMap = Snapshot.getFileMap repositoryId patchSet.BaseDirectoryId correlationId
            let! headMap = Snapshot.getFileMap repositoryId patchSet.HeadDirectoryId correlationId
            let! newBaseMap = Snapshot.getFileMap repositoryId newBaseDirectoryId correlationId
            let newBaseActorProxy = DirectoryVersion.CreateActorProxy newBaseDirectoryId repositoryId correlationId
            let! newBaseDirectoryVersion = newBaseActorProxy.Get correlationId

            let ownerId = newBaseDirectoryVersion.DirectoryVersion.OwnerId
            let organizationId = newBaseDirectoryVersion.DirectoryVersion.OrganizationId

            let conflicts = computeConflicts patchSet baseMap newBaseMap

            if conflicts.Length > 0 then
                return { DirectoryVersions = Array.empty; RootDirectoryId = DirectoryVersionId.Empty; Conflicts = conflicts }
            else
                let mergedMap = Dictionary<RelativePath, FileVersion>(newBaseMap, StringComparer.OrdinalIgnoreCase)

                for patchFile in patchSet.Files do
                    match patchFile.Operation with
                    | PatchOperation.DeleteFile ->
                        mergedMap.Remove(patchFile.RelativePath) |> ignore
                    | PatchOperation.AddFile
                    | PatchOperation.ModifyFile ->
                        match selectFileVersion patchFile baseMap headMap with
                        | Some fileVersion -> mergedMap[patchFile.RelativePath] <- fileVersion
                        | None -> ()
                    | PatchOperation.RenameFile(oldPath, newPath) ->
                        mergedMap.Remove(oldPath) |> ignore
                        if headMap.ContainsKey(newPath) then
                            mergedMap[newPath] <- headMap[newPath]

                let localDirectories = Dictionary<RelativePath, LocalDirectoryVersion>(StringComparer.OrdinalIgnoreCase)
                let rootDirectory = ensureDirectory localDirectories ownerId organizationId repositoryId (RelativePath Constants.RootDirectoryPath)

                for fileVersion in mergedMap.Values do
                    let relativeDirectory = getRelativeDirectory fileVersion.RelativePath Constants.RootDirectoryPath
                    let directory = ensureDirectory localDirectories ownerId organizationId repositoryId (RelativePath relativeDirectory)
                    let localFile = fileVersion.ToLocalFileVersion DateTime.UtcNow
                    directory.Files.Add(localFile)

                    let mutable parentOpt = getParentPath relativeDirectory

                    while parentOpt.IsSome do
                        let parent = ensureDirectory localDirectories ownerId organizationId repositoryId (RelativePath parentOpt.Value)
                        let parentPath = parentOpt.Value
                        parentOpt <- getParentPath parentPath

                for kvp in localDirectories do
                    let directory = kvp.Value
                    if directory.RelativePath <> Constants.RootDirectoryPath then
                        match getParentPath directory.RelativePath with
                        | Some parentPath ->
                            let parent = ensureDirectory localDirectories ownerId organizationId repositoryId (RelativePath parentPath)
                            if not (parent.Directories.Contains(directory.DirectoryVersionId)) then
                                parent.Directories.Add(directory.DirectoryVersionId)
                        | None -> ()

                let directoryById = Dictionary<DirectoryVersionId, LocalDirectoryVersion>()

                for directory in localDirectories.Values do
                    directoryById[directory.DirectoryVersionId] <- directory

                let sortedPaths =
                    localDirectories.Keys
                    |> Seq.sortByDescending countSegments
                    |> Seq.toArray

                for relativePath in sortedPaths do
                    let directory = localDirectories[relativePath]
                    let subdirectories = List<LocalDirectoryVersion>()

                    for id in directory.Directories do
                        if directoryById.ContainsKey(id) then
                            subdirectories.Add(directoryById[id])
                    let directoryHash = computeSha256ForDirectory directory.RelativePath subdirectories directory.Files
                    let directorySize = getLocalDirectorySize directory.Files
                    let updatedDirectory = { directory with Sha256Hash = directoryHash; Size = directorySize }
                    localDirectories[relativePath] <- updatedDirectory
                    directoryById[updatedDirectory.DirectoryVersionId] <- updatedDirectory

                let directoryDtos =
                    localDirectories.Values
                    |> Seq.map (fun directory ->
                        let files = List<FileVersion>()

                        for localFile in directory.Files do
                            match mergedMap.TryGetValue(localFile.RelativePath) with
                            | true, fileVersion -> files.Add(fileVersion)
                            | _ -> files.Add(localFile.ToFileVersion)

                        let directoryVersion =
                            DirectoryVersion.Create
                                directory.DirectoryVersionId
                                directory.OwnerId
                                directory.OrganizationId
                                directory.RepositoryId
                                directory.RelativePath
                                directory.Sha256Hash
                                directory.Directories
                                files
                                directory.Size

                        { DirectoryVersion = directoryVersion
                          RecursiveSize = directory.Size
                          DeletedAt = None
                          DeleteReason = String.Empty
                          HashesValidated = false })
                    |> Seq.toArray

                let rootDirectoryId = localDirectories[Constants.RootDirectoryPath].DirectoryVersionId

                return { DirectoryVersions = directoryDtos; RootDirectoryId = rootDirectoryId; Conflicts = Array.empty }
        }
