namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open Orleans
open System
open System.Runtime.Serialization

module RepositoryContentCounter =

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type RepositoryContentCounterLifecycleState =
        | NotReferenced
        | Referenced

        static member GetKnownTypes() = GetKnownTypes<RepositoryContentCounterLifecycleState>()

    [<KnownType("GetKnownTypes")>]
    type RepositoryContentCounterCommand =
        | AddReference of operationId: RepositoryContentCounterOperationId * repositoryId: RepositoryId * manifestAddress: ManifestAddress
        | RemoveReference of operationId: RepositoryContentCounterOperationId * repositoryId: RepositoryId * manifestAddress: ManifestAddress

        static member GetKnownTypes() = GetKnownTypes<RepositoryContentCounterCommand>()

    [<KnownType("GetKnownTypes")>]
    type RepositoryContentCounterEventType =
        | ReferenceAdded of operationId: RepositoryContentCounterOperationId * repositoryId: RepositoryId * manifestAddress: ManifestAddress
        | ReferenceRemoved of operationId: RepositoryContentCounterOperationId

        static member GetKnownTypes() = GetKnownTypes<RepositoryContentCounterEventType>()

    [<KnownType("GetKnownTypes")>]
    type RepositoryContentCounterIntent =
        | IncrementManifestReferenceCount of repositoryId: RepositoryId * manifestAddress: ManifestAddress
        | DecrementManifestReferenceCount of repositoryId: RepositoryId * manifestAddress: ManifestAddress

        static member GetKnownTypes() = GetKnownTypes<RepositoryContentCounterIntent>()

    type RepositoryContentCounterEvent = { Event: RepositoryContentCounterEventType; Metadata: EventMetadata }

    [<GenerateSerializer>]
    type RepositoryContentCounterDto =
        {
            Class: string
            RepositoryId: RepositoryId
            ManifestAddress: ManifestAddress
            ReferenceCount: ReferenceCount
            LifecycleState: RepositoryContentCounterLifecycleState
            LastOperationId: RepositoryContentCounterOperationId option
        }

        static member Default =
            {
                Class = nameof RepositoryContentCounterDto
                RepositoryId = RepositoryId.Empty
                ManifestAddress = String.Empty
                ReferenceCount = 0L
                LifecycleState = RepositoryContentCounterLifecycleState.NotReferenced
                LastOperationId = None
            }

        static member UpdateDto counterEvent current =
            match counterEvent.Event with
            | RepositoryContentCounterEventType.ReferenceAdded (operationId, repositoryId, manifestAddress) ->
                { current with
                    RepositoryId =
                        if current.RepositoryId = RepositoryId.Empty then
                            repositoryId
                        else
                            current.RepositoryId
                    ManifestAddress =
                        if String.IsNullOrWhiteSpace current.ManifestAddress then
                            manifestAddress
                        else
                            current.ManifestAddress
                    ReferenceCount = current.ReferenceCount + 1L
                    LifecycleState = RepositoryContentCounterLifecycleState.Referenced
                    LastOperationId = Some operationId
                }
            | RepositoryContentCounterEventType.ReferenceRemoved operationId ->
                let nextReferenceCount = max 0L (current.ReferenceCount - 1L)

                { current with
                    ReferenceCount = nextReferenceCount
                    LifecycleState =
                        if nextReferenceCount = 0L then
                            RepositoryContentCounterLifecycleState.NotReferenced
                        else
                            RepositoryContentCounterLifecycleState.Referenced
                    LastOperationId = Some operationId
                }

    [<GenerateSerializer>]
    type RepositoryContentCounterDecision =
        {
            Counter: RepositoryContentCounterDto
            OperationId: RepositoryContentCounterOperationId
            Events: RepositoryContentCounterEvent list
            Intents: RepositoryContentCounterIntent list
            WasIdempotentReplay: bool
            Message: string
        }
