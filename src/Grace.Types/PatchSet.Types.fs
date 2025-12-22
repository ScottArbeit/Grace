namespace Grace.Types

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module PatchSet =

    [<GenerateSerializer>]
    type PathKey =
        { L1: string
          L2: string
          L3: string }

        static member Default = { L1 = String.Empty; L2 = String.Empty; L3 = String.Empty }

    [<GenerateSerializer>]
    type UpdatedPaths =
        { Keys: PathKey array
          ShardHints: int array }

        static member Default = { Keys = Array.empty; ShardHints = Array.empty }

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type PatchOperation =
        | AddFile
        | ModifyFile
        | DeleteFile
        | RenameFile of oldPath: string * newPath: string

        static member GetKnownTypes() = GetKnownTypes<PatchOperation>()

        override this.ToString() = getDiscriminatedUnionFullName this

    [<GenerateSerializer>]
    type PatchFile =
        { RelativePath: RelativePath
          Operation: PatchOperation
          BaseSha256: Sha256Hash option
          HeadSha256: Sha256Hash option }

        static member Default =
            { RelativePath = RelativePath String.Empty
              Operation = PatchOperation.ModifyFile
              BaseSha256 = None
              HeadSha256 = None }

    [<GenerateSerializer>]
    type PatchSetDto =
        { PatchSetId: PatchSetId
          RepositoryId: RepositoryId
          BaseDirectoryId: DirectoryVersionId
          HeadDirectoryId: DirectoryVersionId
          UpdatedPaths: UpdatedPaths
          Files: PatchFile array
          CreatedAt: Instant
          CreatedBy: OwnerId }

        static member Default =
            { PatchSetId = PatchSetId.Empty
              RepositoryId = RepositoryId.Empty
              BaseDirectoryId = DirectoryVersionId.Empty
              HeadDirectoryId = DirectoryVersionId.Empty
              UpdatedPaths = UpdatedPaths.Default
              Files = Array.empty
              CreatedAt = Constants.DefaultTimestamp
              CreatedBy = OwnerId.Empty }

    [<KnownType("GetKnownTypes")>]
    type PatchSetCommand =
        | Create of
            patchSetId: PatchSetId *
            repositoryId: RepositoryId *
            baseDirectoryId: DirectoryVersionId *
            headDirectoryId: DirectoryVersionId *
            updatedPaths: UpdatedPaths *
            files: PatchFile array *
            createdBy: OwnerId

        static member GetKnownTypes() = GetKnownTypes<PatchSetCommand>()

    [<KnownType("GetKnownTypes")>]
    type PatchSetEventType =
        | Created of PatchSetDto

        static member GetKnownTypes() = GetKnownTypes<PatchSetEventType>()

    type PatchSetEvent =
        { Event: PatchSetEventType
          Metadata: EventMetadata }

    type PatchSetDto with
        static member UpdateDto patchSetEvent currentDto =
            match patchSetEvent.Event with
            | Created patchSetDto -> { patchSetDto with CreatedAt = patchSetEvent.Metadata.Timestamp }
