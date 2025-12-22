namespace Grace.Server.Services

open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.PatchSet
open Grace.Types.Types
open System
open System.Collections.Generic
open System.Linq
open System.Threading.Tasks

module PatchSetService =

    let private diffFileMaps (baseMap: Dictionary<RelativePath, FileVersion>) (headMap: Dictionary<RelativePath, FileVersion>) =
        let files = List<PatchFile>()
        let updatedPaths = List<string>()

        let baseKeys = HashSet<RelativePath>(baseMap.Keys, StringComparer.OrdinalIgnoreCase)
        let headKeys = HashSet<RelativePath>(headMap.Keys, StringComparer.OrdinalIgnoreCase)

        for relativePath in baseKeys do
            if not (headKeys.Contains(relativePath)) then
                files.Add(
                    { RelativePath = relativePath
                      Operation = PatchOperation.DeleteFile
                      BaseSha256 = Some baseMap[relativePath].Sha256Hash
                      HeadSha256 = None }
                )

                updatedPaths.Add(relativePath)

        for relativePath in headKeys do
            if not (baseKeys.Contains(relativePath)) then
                files.Add(
                    { RelativePath = relativePath
                      Operation = PatchOperation.AddFile
                      BaseSha256 = None
                      HeadSha256 = Some headMap[relativePath].Sha256Hash }
                )

                updatedPaths.Add(relativePath)
            else
                let baseFile = baseMap[relativePath]
                let headFile = headMap[relativePath]

                if baseFile.Sha256Hash <> headFile.Sha256Hash then
                    files.Add(
                        { RelativePath = relativePath
                          Operation = PatchOperation.ModifyFile
                          BaseSha256 = Some baseFile.Sha256Hash
                          HeadSha256 = Some headFile.Sha256Hash }
                    )

                    updatedPaths.Add(relativePath)

        files, updatedPaths

    let createPatchSet
        (repositoryId: RepositoryId)
        (baseDirectoryId: DirectoryVersionId)
        (headDirectoryId: DirectoryVersionId)
        (createdBy: OwnerId)
        (correlationId: CorrelationId)
        =
        task {
            let! baseMap = Snapshot.getFileMap repositoryId baseDirectoryId correlationId
            let! headMap = Snapshot.getFileMap repositoryId headDirectoryId correlationId

            let files, updatedPaths = diffFileMaps baseMap headMap
            let updatedPathsDto = UpdatedPaths.compute updatedPaths

            let dto =
                { PatchSetDto.Default with
                    PatchSetId = PatchSetId.NewGuid()
                    RepositoryId = repositoryId
                    BaseDirectoryId = baseDirectoryId
                    HeadDirectoryId = headDirectoryId
                    UpdatedPaths = updatedPathsDto
                    Files = files.ToArray()
                    CreatedAt = getCurrentInstant ()
                    CreatedBy = createdBy }

            return dto
        }
