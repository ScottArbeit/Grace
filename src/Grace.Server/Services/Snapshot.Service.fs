namespace Grace.Server.Services

open Grace.Actors.Extensions.ActorProxy
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.DirectoryVersion
open Grace.Types.Types
open System
open System.Collections.Generic

module Snapshot =

    let getFileMap (repositoryId: RepositoryId) (directoryVersionId: DirectoryVersionId) (correlationId: CorrelationId) =
        task {
            let actorProxy = DirectoryVersion.CreateActorProxy directoryVersionId repositoryId correlationId
            let! directoryVersions = actorProxy.GetRecursiveDirectoryVersions false correlationId

            let fileMap = Dictionary<RelativePath, FileVersion>(StringComparer.OrdinalIgnoreCase)

            for directoryVersionDto in directoryVersions do
                for file in directoryVersionDto.DirectoryVersion.Files do
                    fileMap[file.RelativePath] <- file

            return fileMap
        }
