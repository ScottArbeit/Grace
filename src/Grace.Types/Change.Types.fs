namespace Grace.Types

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Types
open Grace.Types.PatchSet
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Change =

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ChangeStatus =
        | Open
        | InTrain of PromotionTrainId
        | Promoted
        | Abandoned

        static member GetKnownTypes() = GetKnownTypes<ChangeStatus>()

        override this.ToString() = getDiscriminatedUnionFullName this

    [<GenerateSerializer>]
    type ChangeRevision =
        { ReferenceId: ReferenceId
          PatchSetId: PatchSetId
          CreatedAt: Instant
          Supersedes: ReferenceId option }

        static member Default =
            { ReferenceId = ReferenceId.Empty
              PatchSetId = PatchSetId.Empty
              CreatedAt = Constants.DefaultTimestamp
              Supersedes = None }

    [<GenerateSerializer>]
    type ChangeDto =
        { ChangeId: ChangeId
          RepositoryId: RepositoryId
          Title: string
          Description: string
          Status: ChangeStatus
          Revisions: ChangeRevision array
          CurrentRevision: ReferenceId
          UpdatedPaths: UpdatedPaths }

        static member Default =
            { ChangeId = ChangeId.Empty
              RepositoryId = RepositoryId.Empty
              Title = String.Empty
              Description = String.Empty
              Status = ChangeStatus.Open
              Revisions = Array.empty
              CurrentRevision = ReferenceId.Empty
              UpdatedPaths = UpdatedPaths.Default }

    [<KnownType("GetKnownTypes")>]
    type ChangeCommand =
        | Create of
            changeId: ChangeId *
            repositoryId: RepositoryId *
            title: string *
            description: string *
            initialRevision: ChangeRevision option *
            updatedPaths: UpdatedPaths option
        | AddRevision of revision: ChangeRevision * updatedPaths: UpdatedPaths
        | SetCurrentRevision of referenceId: ReferenceId * updatedPaths: UpdatedPaths
        | UpdateMetadata of title: string * description: string
        | SetStatus of status: ChangeStatus

        static member GetKnownTypes() = GetKnownTypes<ChangeCommand>()

    [<KnownType("GetKnownTypes")>]
    type ChangeEventType =
        | Created of ChangeId * RepositoryId * string * string
        | RevisionAdded of ChangeRevision * UpdatedPaths
        | CurrentRevisionSet of ReferenceId * UpdatedPaths
        | MetadataUpdated of string * string
        | StatusSet of ChangeStatus

        static member GetKnownTypes() = GetKnownTypes<ChangeEventType>()

    type ChangeEvent =
        { Event: ChangeEventType
          Metadata: EventMetadata }

    type ChangeDto with
        static member UpdateDto changeEvent currentDto =
            match changeEvent.Event with
            | Created(changeId, repositoryId, title, description) ->
                { currentDto with
                    ChangeId = changeId
                    RepositoryId = repositoryId
                    Title = title
                    Description = description
                    Status = ChangeStatus.Open }
            | RevisionAdded(revision, updatedPaths) ->
                let revisions = Array.append currentDto.Revisions [| revision |]
                { currentDto with
                    Revisions = revisions
                    CurrentRevision = revision.ReferenceId
                    UpdatedPaths = updatedPaths }
            | CurrentRevisionSet(referenceId, updatedPaths) ->
                { currentDto with CurrentRevision = referenceId; UpdatedPaths = updatedPaths }
            | MetadataUpdated(title, description) ->
                { currentDto with Title = title; Description = description }
            | StatusSet status -> { currentDto with Status = status }
