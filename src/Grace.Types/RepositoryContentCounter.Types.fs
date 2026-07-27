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

    /// Identifies the bounded counter operation recorded by the latest completed change.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type RepositoryContentCounterChangeOperation =
        | Added
        | Removed

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<RepositoryContentCounterChangeOperation>()

    /// Records the latest completed count transition without retaining a lifetime operation history.
    [<GenerateSerializer>]
    type RepositoryContentCounterCompletedChange =
        {
            OperationId: RepositoryContentCounterOperationId
            Operation: RepositoryContentCounterChangeOperation
            PreviousCount: ReferenceCount
            CurrentCount: ReferenceCount
            Revision: int64
        }

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
        | IncrementManifestReferenceCount of
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress *
            counterRevision: int64
        | DecrementManifestReferenceCount of
            repositoryId: RepositoryId *
            storagePoolId: StoragePoolId *
            manifestAddress: ManifestAddress *
            counterRevision: int64

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
            Count: ReferenceCount
            Revision: int64
            LastCompletedChange: RepositoryContentCounterCompletedChange option
        }

        /// Preserves the established read name while the durable snapshot stores the contract's `Count` field.
        member this.ReferenceCount = this.Count

        /// Derives lifecycle from the bounded current count instead of persisting duplicate state.
        member this.LifecycleState =
            if this.Count = 0L then
                RepositoryContentCounterLifecycleState.NotReferenced
            else
                RepositoryContentCounterLifecycleState.Referenced

        /// Projects the latest operation identity for existing callers without persisting another field.
        member this.LastOperationId =
            this.LastCompletedChange
            |> Option.map (fun change -> change.OperationId)

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof RepositoryContentCounterDto
                RepositoryId = RepositoryId.Empty
                StoragePoolId = String.Empty
                ManifestAddress = String.Empty
                Count = 0L
                Revision = 0L
                LastCompletedChange = None
            }

        /// Creates the DTO shape used to carry partial updates without mutating the persisted aggregate directly.
        static member UpdateDto counterEvent current =
            match counterEvent.Event with
            | RepositoryContentCounterEventType.ReferenceAdded (operationId, repositoryId, storagePoolId, manifestAddress) ->
                let nextCount = current.Count + 1L

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
                    Count = nextCount
                    Revision = current.Revision + 1L
                    LastCompletedChange =
                        Some
                            {
                                OperationId = operationId
                                Operation = RepositoryContentCounterChangeOperation.Added
                                PreviousCount = current.Count
                                CurrentCount = nextCount
                                Revision = current.Revision + 1L
                            }
                }
            | RepositoryContentCounterEventType.ReferenceRemoved operationId ->
                let nextReferenceCount = max 0L (current.Count - 1L)

                { current with
                    Count = nextReferenceCount
                    Revision = current.Revision + 1L
                    LastCompletedChange =
                        Some
                            {
                                OperationId = operationId
                                Operation = RepositoryContentCounterChangeOperation.Removed
                                PreviousCount = current.Count
                                CurrentCount = nextReferenceCount
                                Revision = current.Revision + 1L
                            }
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
