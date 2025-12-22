namespace Grace.Types

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Operation =

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type OperationStatus =
        | Pending
        | Running
        | NeedsUserInput
        | Succeeded
        | Failed

        static member GetKnownTypes() = GetKnownTypes<OperationStatus>()

        override this.ToString() = getDiscriminatedUnionFullName this

    [<GenerateSerializer>]
    type OperationResult =
        { NewReferenceIds: ReferenceId array
          NewDirectoryIds: DirectoryVersionId array
          Message: string }

        static member Default =
            { NewReferenceIds = Array.empty
              NewDirectoryIds = Array.empty
              Message = String.Empty }

    [<GenerateSerializer>]
    type OperationDto =
        { OperationId: OperationId
          RepositoryId: RepositoryId
          Status: OperationStatus
          Percent: int
          StartedAt: Instant option
          CompletedAt: Instant option
          Result: OperationResult option
          Error: string option }

        static member Default =
            { OperationId = OperationId.Empty
              RepositoryId = RepositoryId.Empty
              Status = OperationStatus.Pending
              Percent = 0
              StartedAt = None
              CompletedAt = None
              Result = None
              Error = None }

    [<KnownType("GetKnownTypes")>]
    type OperationCommand =
        | Create of operationId: OperationId * repositoryId: RepositoryId
        | Start
        | UpdateProgress of percent: int * message: string option
        | NeedsInput of message: string option
        | Complete of result: OperationResult
        | Fail of error: string
        | Cancel

        static member GetKnownTypes() = GetKnownTypes<OperationCommand>()

    [<KnownType("GetKnownTypes")>]
    type OperationEventType =
        | Created of operationId: OperationId * repositoryId: RepositoryId
        | Started
        | ProgressUpdated of percent: int * message: string option
        | NeedsUserInput of message: string option
        | Completed of OperationResult
        | Failed of error: string
        | Cancelled

        static member GetKnownTypes() = GetKnownTypes<OperationEventType>()

    type OperationEvent =
        { Event: OperationEventType
          Metadata: EventMetadata }

    type OperationDto with
        static member UpdateDto operationEvent currentDto =
            match operationEvent.Event with
            | Created(operationId, repositoryId) ->
                { currentDto with
                    OperationId = operationId
                    RepositoryId = repositoryId
                    Status = OperationStatus.Pending }
            | Started ->
                { currentDto with
                    Status = OperationStatus.Running
                    StartedAt = Some operationEvent.Metadata.Timestamp }
            | ProgressUpdated(percent, _message) ->
                { currentDto with Percent = percent }
            | NeedsUserInput message ->
                { currentDto with Status = OperationStatus.NeedsUserInput; Error = message }
            | Completed result ->
                { currentDto with
                    Status = OperationStatus.Succeeded
                    CompletedAt = Some operationEvent.Metadata.Timestamp
                    Result = Some result }
            | Failed error ->
                { currentDto with
                    Status = OperationStatus.Failed
                    CompletedAt = Some operationEvent.Metadata.Timestamp
                    Error = Some error }
            | Cancelled ->
                { currentDto with
                    Status = OperationStatus.Failed
                    CompletedAt = Some operationEvent.Metadata.Timestamp
                    Error = Some "Cancelled" }
