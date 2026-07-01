namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Orleans
open System
open System.Runtime.Serialization

/// Contains repository content counter helpers.
module RepositoryContentCounter =

    /// Represents repository content counter lifecycle state.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type RepositoryContentCounterLifecycleState =
        | NotReferenced
        | Referenced

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<RepositoryContentCounterLifecycleState>()

    /// Represents repository content counter command.
    [<KnownType("GetKnownTypes")>]
    type RepositoryContentCounterCommand =
        | AddReference of
            operationId: RepositoryContentCounterOperationId *
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress
        | RemoveReference of
            operationId: RepositoryContentCounterOperationId *
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<RepositoryContentCounterCommand>()

    /// Represents repository content counter event type.
    [<KnownType("GetKnownTypes")>]
    type RepositoryContentCounterEventType =
        | ReferenceAdded of
            operationId: RepositoryContentCounterOperationId *
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress
        | ReferenceRemoved of operationId: RepositoryContentCounterOperationId

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<RepositoryContentCounterEventType>()

    /// Represents repository content counter intent.
    [<KnownType("GetKnownTypes")>]
    type RepositoryContentCounterIntent =
        | IncrementManifestReferenceCount of repositoryId: RepositoryId * storagePoolId: StoragePoolId * manifestAddress: ManifestAddress
        | DecrementManifestReferenceCount of repositoryId: RepositoryId * storagePoolId: StoragePoolId * manifestAddress: ManifestAddress

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<RepositoryContentCounterIntent>()

    /// Represents the repository content counter event contract.
    type RepositoryContentCounterEvent = { Event: RepositoryContentCounterEventType; Metadata: EventMetadata }

    /// Represents repository content counter dto.
    [<GenerateSerializer>]
    type RepositoryContentCounterDto =
        {
            Class: string
            RepositoryId: RepositoryId
            StoragePoolId: StoragePoolId
            ManifestAddress: ManifestAddress
            ReferenceCount: ReferenceCount
            LifecycleState: RepositoryContentCounterLifecycleState
            LastOperationId: RepositoryContentCounterOperationId option
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof RepositoryContentCounterDto
                RepositoryId = RepositoryId.Empty
                StoragePoolId = String.Empty
                ManifestAddress = String.Empty
                ReferenceCount = 0L
                LifecycleState = RepositoryContentCounterLifecycleState.NotReferenced
                LastOperationId = None
            }

        /// Creates the DTO shape used to carry partial updates without mutating the persisted aggregate directly.
        static member UpdateDto counterEvent current =
            match counterEvent.Event with
            | RepositoryContentCounterEventType.ReferenceAdded (operationId, repositoryId, storagePoolId, manifestAddress) ->
                { current with
                    RepositoryId =
                        if current.RepositoryId = RepositoryId.Empty then
                            repositoryId
                        else
                            current.RepositoryId
                    StoragePoolId =
                        if String.IsNullOrWhiteSpace current.StoragePoolId then
                            storagePoolId
                        else
                            current.StoragePoolId
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

    /// Represents repository content counter decision.
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
