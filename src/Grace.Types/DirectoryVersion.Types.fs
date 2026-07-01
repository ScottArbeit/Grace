namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Repository
open Grace.Types.Common
open NodaTime
open MessagePack
open Orleans
open System
open System.Collections.Generic
open System.Runtime.Serialization

/// Contains directory version helpers.
module DirectoryVersion =

    /// The state held in the database when creating a physical deletion reminder for a DirectoryVersion.
    type PhysicalDeletionReminderState = { DeleteReason: DeleteReason; CorrelationId: CorrelationId }

    /// Represents directory version command.
    [<KnownType("GetKnownTypes")>]
    type DirectoryVersionCommand =
        | Create of directoryVersion: DirectoryVersion * repositoryDto: RepositoryDto
        | SetRecursiveSize of recursizeSize: int64
        | DeleteLogical of DeleteReason: DeleteReason
        | DeletePhysical
        | Undelete

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<DirectoryVersionCommand>()

    /// Defines the events for the DirectoryVersion actor.
    [<KnownType("GetKnownTypes")>]
    type DirectoryVersionEventType =
        | Created of directoryVersion: DirectoryVersion
        | RecursiveSizeSet of recursiveSize: int64
        | LogicalDeleted of DeleteReason: DeleteReason
        | PhysicalDeleted
        | Undeleted

    /// Record that holds the event type and metadata for a DirectoryVersion event.
    type DirectoryVersionEvent =
        {
            /// The DirectoryVersionEventType case that describes the event.
            Event: DirectoryVersionEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }

    /// The DirectoryVersionDto is a data transfer object that represents a directory version in the system.
    [<MessagePackObject>]
    type DirectoryVersionDto =
        {
            [<Key(0)>]
            DirectoryVersion: DirectoryVersion
            [<Key(1)>]
            RecursiveSize: int64
            [<Key(2)>]
            DeletedAt: Instant option
            [<Key(3)>]
            DeleteReason: DeleteReason
            [<Key(4)>]
            HashesValidated: bool
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                DirectoryVersion = DirectoryVersion.Default
                RecursiveSize = Constants.InitialDirectorySize
                DeletedAt = None
                DeleteReason = String.Empty
                HashesValidated = false
            }

        /// Creates the DTO shape used to carry partial updates without mutating the persisted aggregate directly.
        static member UpdateDto directoryVersionEvent currentDirectoryVersionDto =
            let directoryVersionEventType = directoryVersionEvent.Event

            match directoryVersionEventType with
            | Created directoryVersion -> { currentDirectoryVersionDto with DirectoryVersion = directoryVersion }
            | RecursiveSizeSet recursiveSize -> { currentDirectoryVersionDto with RecursiveSize = recursiveSize }
            | LogicalDeleted deleteReason -> { currentDirectoryVersionDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }
            | PhysicalDeleted -> currentDirectoryVersionDto // Do nothing because it's about to be deleted anyway.
            | Undeleted -> { currentDirectoryVersionDto with DeletedAt = None; DeleteReason = String.Empty }
