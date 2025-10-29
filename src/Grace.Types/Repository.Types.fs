namespace Grace.Types

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Repository =

    /// The state held in the database when creating a physical deletion reminder for a repository.
    type PhysicalDeletionReminderState = { DeleteReason: DeleteReason; CorrelationId: CorrelationId }

    [<KnownType("GetKnownTypes")>]
    type RepositoryCommand =
        | Create of
            repositoryName: RepositoryName *
            repositoryId: RepositoryId *
            ownerId: OwnerId *
            organizationId: OrganizationId *
            objectStorageProvider: ObjectStorageProvider
        | Initialize
        | SetObjectStorageProvider of objectStorageProvider: ObjectStorageProvider
        | SetStorageAccountName of storageAccountName: StorageAccountName
        | SetStorageContainerName of storageContainerName: StorageContainerName
        | SetRepositoryType of repositoryVisibility: RepositoryType
        | SetRepositoryStatus of repositoryStatus: RepositoryStatus
        | SetRecordSaves of recordSaves: bool
        | SetAllowsLargeFiles of allowsLargeFiles: bool
        | SetAnonymousAccess of anonymousAccess: bool
        | SetDefaultServerApiVersion of defaultServerApiVersion: string
        | SetDefaultBranchName of defaultBranchName: BranchName
        | SetLogicalDeleteDays of duration: single
        | SetSaveDays of duration: single
        | SetCheckpointDays of duration: single
        | SetDirectoryVersionCacheDays of duration: single
        | SetDiffCacheDays of duration: single
        | SetName of repositoryName: RepositoryName
        | SetDescription of description: string
        | DeleteLogical of force: bool * DeleteReason: DeleteReason
        | DeletePhysical
        | Undelete

        static member GetKnownTypes() = GetKnownTypes<RepositoryCommand>()

    /// Defines the events for the Repository actor.
    [<KnownType("GetKnownTypes")>]
    type RepositoryEventType =
        | Created of
            repositoryName: RepositoryName *
            repositoryId: RepositoryId *
            ownerId: OwnerId *
            organizationId: OrganizationId *
            objectStorageProvider: ObjectStorageProvider
        | Initialized
        | ObjectStorageProviderSet of objectStorageProvider: ObjectStorageProvider
        | StorageAccountNameSet of storageAccountName: StorageAccountName
        | StorageContainerNameSet of storageContainerName: StorageContainerName
        | RepositoryTypeSet of repositoryVisibility: RepositoryType
        | RepositoryStatusSet of repositoryStatus: RepositoryStatus
        | AllowsLargeFilesSet of allowsLargeFiles: bool
        | AnonymousAccessSet of anonymousAccess: bool
        | RecordSavesSet of recordSaves: bool
        | DefaultServerApiVersionSet of defaultServerApiVersion: string
        | DefaultBranchNameSet of defaultBranchName: BranchName
        | LogicalDeleteDaysSet of duration: single
        | SaveDaysSet of duration: single
        | CheckpointDaysSet of duration: single
        | DirectoryVersionCacheDaysSet of duration: single
        | DiffCacheDaysSet of duration: single
        | NameSet of repositoryName: RepositoryName
        | DescriptionSet of description: string
        | LogicalDeleted of force: bool * DeleteReason: DeleteReason
        | PhysicalDeleted
        | Undeleted

        static member GetKnownTypes() = GetKnownTypes<RepositoryEventType>()

    /// Record that holds the event type and metadata for a Repository event.
    type RepositoryEvent =
        {
            /// The RepositoryEventType case that describes the event.
            Event: RepositoryEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }

    /// The RepositoryDto is a data transfer object that represents a repository in the system.
    type RepositoryDto =
        { Class: string
          RepositoryId: RepositoryId
          OwnerId: OwnerId
          OrganizationId: OrganizationId
          RepositoryName: RepositoryName
          ObjectStorageProvider: ObjectStorageProvider
          StorageAccountName: StorageAccountName
          StorageContainerName: StorageContainerName
          RepositoryType: RepositoryType
          RepositoryStatus: RepositoryStatus
          AnonymousAccess: bool
          AllowsLargeFiles: bool
          DefaultServerApiVersion: string
          DefaultBranchName: BranchName
          LogicalDeleteDays: single
          SaveDays: single
          CheckpointDays: single
          DirectoryVersionCacheDays: single
          DiffCacheDays: single
          Description: string
          RecordSaves: bool
          CreatedAt: Instant
          InitializedAt: Instant option
          UpdatedAt: Instant option
          DeletedAt: Instant option
          DeleteReason: DeleteReason }

        static member Default =
            { Class = nameof RepositoryDto
              RepositoryId = Guid.Empty
              OwnerId = OwnerId.Empty
              OrganizationId = OrganizationId.Empty
              RepositoryName = RepositoryName String.Empty
              ObjectStorageProvider = ObjectStorageProvider.Unknown
              StorageAccountName = String.Empty
              StorageContainerName = "grace-objects"
              RepositoryType = RepositoryType.Private
              RepositoryStatus = RepositoryStatus.Active
              AnonymousAccess = false
              AllowsLargeFiles = false
              DefaultServerApiVersion = "latest"
              DefaultBranchName = BranchName Constants.InitialBranchName
              LogicalDeleteDays = 30.0f
              SaveDays = 7.0f
              CheckpointDays = 365.0f
              DirectoryVersionCacheDays = 1.0f
              DiffCacheDays = 1.0f
              Description = String.Empty
              RecordSaves = true
              CreatedAt = Constants.DefaultTimestamp
              InitializedAt = None
              UpdatedAt = None
              DeletedAt = None
              DeleteReason = String.Empty }

        /// Updates a RepositoryDto based on the provided RepositoryEvent.
        static member UpdateDto repositoryEvent currentRepositoryDto =
            let newRepositoryDto =
                match repositoryEvent.Event with
                | Created(name, repositoryId, ownerId, organizationId, objectStorageProvider) ->
                    { RepositoryDto.Default with
                        RepositoryName = name
                        RepositoryId = repositoryId
                        OwnerId = ownerId
                        OrganizationId = organizationId
                        ObjectStorageProvider = objectStorageProvider
                        StorageAccountName = DefaultObjectStorageAccount
                        StorageContainerName = $"{repositoryId}"
                        CreatedAt = repositoryEvent.Metadata.Timestamp }
                | Initialized -> { currentRepositoryDto with InitializedAt = Some(getCurrentInstant ()) }
                | ObjectStorageProviderSet objectStorageProvider -> { currentRepositoryDto with ObjectStorageProvider = objectStorageProvider }
                | StorageAccountNameSet storageAccountName -> { currentRepositoryDto with StorageAccountName = storageAccountName }
                | StorageContainerNameSet containerName -> { currentRepositoryDto with StorageContainerName = containerName }
                | RepositoryStatusSet repositoryStatus -> { currentRepositoryDto with RepositoryStatus = repositoryStatus }
                | RepositoryTypeSet repositoryType -> { currentRepositoryDto with RepositoryType = repositoryType }
                | RecordSavesSet recordSaves -> { currentRepositoryDto with RecordSaves = recordSaves }
                | DefaultServerApiVersionSet version -> { currentRepositoryDto with DefaultServerApiVersion = version }
                | DefaultBranchNameSet defaultBranchName -> { currentRepositoryDto with DefaultBranchName = defaultBranchName }
                | LogicalDeleteDaysSet days -> { currentRepositoryDto with LogicalDeleteDays = days }
                | SaveDaysSet days -> { currentRepositoryDto with SaveDays = days }
                | CheckpointDaysSet days -> { currentRepositoryDto with CheckpointDays = days }
                | DirectoryVersionCacheDaysSet days -> { currentRepositoryDto with DirectoryVersionCacheDays = days }
                | DiffCacheDaysSet days -> { currentRepositoryDto with DiffCacheDays = days }
                | NameSet repositoryName -> { currentRepositoryDto with RepositoryName = repositoryName }
                | DescriptionSet description -> { currentRepositoryDto with Description = description }
                | LogicalDeleted _ -> { currentRepositoryDto with DeletedAt = Some(getCurrentInstant ()) }
                | PhysicalDeleted -> currentRepositoryDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> { currentRepositoryDto with DeletedAt = None; DeleteReason = String.Empty }
                | AllowsLargeFilesSet allowsLargeFiles -> { currentRepositoryDto with AllowsLargeFiles = allowsLargeFiles }
                | AnonymousAccessSet anonymousAccess -> { currentRepositoryDto with AnonymousAccess = anonymousAccess }

            { newRepositoryDto with UpdatedAt = Some repositoryEvent.Metadata.Timestamp }
