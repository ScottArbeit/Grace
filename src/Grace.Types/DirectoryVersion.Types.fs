namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open MessagePack
open Orleans
open System
open System.Collections.Generic
open System.Runtime.Serialization

module DirectoryVersion =

    /// The state held in the database when creating a physical deletion reminder for a DirectoryVersion.
    type PhysicalDeletionReminderState = { DeleteReason: DeleteReason; CorrelationId: CorrelationId }

    [<KnownType("GetKnownTypes")>]
    type DirectoryVersionCommand =
        | Create of directoryVersion: DirectoryVersion
        | SetRecursiveSize of recursizeSize: int64
        | DeleteLogical of DeleteReason: DeleteReason
        | DeletePhysical
        | Undelete

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
        { [<Key(0)>]
          DirectoryVersion: DirectoryVersion
          [<Key(1)>]
          RecursiveSize: int64
          [<Key(2)>]
          DeletedAt: Instant option
          [<Key(3)>]
          DeleteReason: DeleteReason }

        static member Default =
            { DirectoryVersion = DirectoryVersion.Default; RecursiveSize = Constants.InitialDirectorySize; DeletedAt = None; DeleteReason = String.Empty }

        static member UpdateDto directoryVersionEvent currentDirectoryVersionDto =
            let directoryVersionEventType = directoryVersionEvent.Event

            match directoryVersionEventType with
            | Created directoryVersion -> { currentDirectoryVersionDto with DirectoryVersion = directoryVersion }
            | RecursiveSizeSet recursiveSize -> { currentDirectoryVersionDto with RecursiveSize = recursiveSize }
            | LogicalDeleted deleteReason -> { currentDirectoryVersionDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }
            | PhysicalDeleted -> currentDirectoryVersionDto // Do nothing because it's about to be deleted anyway.
            | Undeleted -> { currentDirectoryVersionDto with DeletedAt = None; DeleteReason = String.Empty }
