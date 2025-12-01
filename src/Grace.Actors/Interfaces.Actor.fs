namespace Grace.Actors

open Grace.Actors.Types
open Grace.Shared
open Grace.Types.Branch
open Grace.Types.Diff
open Grace.Types.DirectoryVersion
open Grace.Types.PromotionGroup
open Grace.Types.Reference
open Grace.Types.Reminder
open Grace.Types.Repository
open Grace.Types.Organization
open Grace.Types.Owner
open Grace.Types.Types
open Grace.Shared.Utilities
open NodaTime
open Orleans
open System
open System.Collections.Generic
open System.Threading.Tasks
open System.Reflection.Metadata
open Orleans.Runtime

module Interfaces =

    type PersistAction =
        | Save
        | DoNotSave

    type ExportError =
        | EventListIsEmpty
        | Exception of ExceptionResponse

    type ImportError =
        | EventListIsEmpty
        | Exception of ExceptionResponse

    type RevertError =
        | EmptyEventList
        | OutOfRange
        | Exception of ExceptionResponse

    /// Retrieves the RepositoryId for a grain.
    /// This is used when computing the PartitionKey for grains in a repository during storage operations.
    [<Interface>]
    type IGrainRepositoryIdExtension =
        inherit IGrainExtension

        /// Retrieves the RepositoryId for a grain.
        abstract member GetRepositoryId: correlationId: CorrelationId -> Task<RepositoryId>

    /// Retrieves the RepositoryId for a grain.
    /// This is used when computing the PartitionKey for grains in a repository during storage operations.
    [<Interface>]
    type IHasRepositoryId =
        /// Gets the RepositoryId for this actor.
        abstract member GetRepositoryId: correlationId: CorrelationId -> Task<RepositoryId>

    /// This is an experimental interface to explore how to back up and rehydrate actor instances.
    [<Interface>]
    type IExportable<'T> =
        abstract member Export: unit -> Task<Result<List<'T>, ExportError>>
        abstract member Import: IReadOnlyList<'T> -> Task<Result<int, ImportError>>

    /// This is an experimental interface to explore how to implement important management functions for actors that we'll need in production.
    [<Interface>]
    type IRevertable<'T> =
        abstract member EventCount: unit -> Task<int>
        abstract member RevertToInstant: Instant -> PersistAction -> Task<Result<'T, RevertError>>
        abstract member RevertBack: int -> PersistAction -> Task<Result<'T, RevertError>>

    /// Defines the operations that an actor must implement to handle Grace reminders.
    [<Interface>]
    type IGraceReminderWithGuidKey =
        inherit IGrainWithGuidKey
        /// Receives a reminder and processes it asynchronously.
        abstract member ReceiveReminderAsync: reminder: ReminderDto -> Task<Result<unit, GraceError>>
        /// Schedules a reminder to be sent to the actor after a specified delay.
        abstract member ScheduleReminderAsync: reminderType: ReminderTypes -> delay: Duration -> state: ReminderState -> correlationId: CorrelationId -> Task

    /// Defines the operations that an actor must implement to handle Grace reminders.
    [<Interface>]
    type IGraceReminderWithStringKey =
        inherit IGrainWithStringKey
        /// Receives a reminder and processes it asynchronously.
        abstract member ReceiveReminderAsync: reminder: ReminderDto -> Task<Result<unit, GraceError>>
        /// Schedules a reminder to be sent to the actor after a specified delay.
        abstract member ScheduleReminderAsync: reminderType: ReminderTypes -> delay: Duration -> state: ReminderState -> correlationId: CorrelationId -> Task

    /// Defines the operations for the Branch actor.
    [<Interface>]
    type IBranchActor =
        inherit IGraceReminderWithGuidKey

        /// Validates that a branch with this BranchId exists.
        abstract member Exists: correlationId: CorrelationId -> Task<bool>

        /// Retrieves the current state of the branch.
        abstract member Get: correlationId: CorrelationId -> Task<BranchDto>

        /// Retrieves the list of events handled by this branch.
        abstract member GetEvents: correlationId: CorrelationId -> Task<IReadOnlyList<BranchEvent>>

        /// Retrieves the most recent commit from this branch.
        abstract member GetLatestCommit: correlationId: CorrelationId -> Task<ReferenceDto>

        /// Retrieves the most recent promotion from this branch.
        abstract member GetLatestPromotion: correlationId: CorrelationId -> Task<ReferenceDto>

        /// Retrieves the parent branch for a given branch.
        abstract member GetParentBranch: correlationId: CorrelationId -> Task<BranchDto>

        /// Validates incoming commands and converts them to events that are stored in the database.
        abstract member Handle: command: BranchCommand -> eventMetadata: EventMetadata -> Task<GraceResult<string>>

        /// Returns true if this branch has been deleted.
        abstract member IsDeleted: correlationId: CorrelationId -> Task<bool>

        /// Marks the branch as needing to recompute its latest references.
        abstract member MarkForRecompute: correlationId: CorrelationId -> Task

    /// Defines the operations for the BranchName actor.
    [<Interface>]
    type IBranchNameActor =
        inherit IGrainWithStringKey

        /// Returns the BranchId for the given BranchName.
        abstract member GetBranchId: correlationId: CorrelationId -> Task<BranchId option>

        /// Sets the BranchId that matches the BranchName.
        abstract member SetBranchId: branchId: BranchId -> correlationId: CorrelationId -> Task

    /// Defines the operations for the Diff actor.
    [<Interface>]
    type IDiffActor =
        inherit IGraceReminderWithStringKey

        /// Populates the contents of the diff without returning the results.
        abstract member Compute: correlationId: CorrelationId -> Task<GraceResult<string>>

        /// Gets the results of the diff. If the diff has not already been computed, it will be computed.
        abstract member GetDiff: correlationId: CorrelationId -> Task<DiffDto>

    /// Defines the operations for the DirectoryAppearance actor.
    [<Interface>]
    type IDirectoryAppearanceActor =
        inherit IGrainWithGuidKey

        /// Adds an appearance to the directory appearance list.
        abstract member Add: appearance: Appearance -> correlationId: CorrelationId -> Task

        /// Removes an appearance from the directory appearance list.
        abstract member Remove: appearance: Appearance -> correlationId: CorrelationId -> Task

        /// Checks if the directory appearance list contains the given appearance.
        abstract member Contains: appearance: Appearance -> correlationId: CorrelationId -> Task<bool>

        /// Returns the sorted set of appearances for this directory.
        abstract member Appearances: correlationId: CorrelationId -> Task<SortedSet<Appearance>>

    ///Defines the operations for the DirectoryVersion actor.
    [<Interface>]
    type IDirectoryVersionActor =
        inherit IGraceReminderWithGuidKey

        /// Returns true if the actor instance already exists.
        abstract member Exists: correlationId: CorrelationId -> Task<bool>

        /// Returns the DirectoryVersion instance for this directory.
        abstract member Get: correlationId: CorrelationId -> Task<DirectoryVersionDto>

        /// Returns the list of subdirectories contained in this directory.
        abstract member GetCreatedAt: correlationId: CorrelationId -> Task<Instant>

        /// Returns the list of subdirectories contained in this directory.
        abstract member GetDirectories: correlationId: CorrelationId -> Task<List<DirectoryVersionId>>

        /// Returns the list of files contained in this directory.
        abstract member GetFiles: correlationId: CorrelationId -> Task<List<FileVersion>>

        /// Returns the Sha256 hash value for this directory.
        abstract member GetSha256Hash: correlationId: CorrelationId -> Task<Sha256Hash>

        /// Returns the total size of files contained in this directory. This does not include files in subdirectories; for that, use GetSizeRecursive().
        abstract member GetSize: correlationId: CorrelationId -> Task<int64>

        /// Returns a list of DirectoryVersion objects for all subdirectories.
        abstract member GetRecursiveDirectoryVersions: forceRegenerate: bool -> correlationId: CorrelationId -> Task<DirectoryVersionDto array>

        /// Returns the total size of files contained in this directory and all subdirectories.
        abstract member GetRecursiveSize: correlationId: CorrelationId -> Task<int64>

        /// Returns the Uri, with a shared access signature, to download the .zip file containing the contents of this directory and all subdirectories. If the .zip file doesn't exist, it will be created.
        abstract member GetZipFileUri: correlationId: CorrelationId -> Task<UriWithSharedAccessSignature>

        /// Delete the DirectoryVersion and all subdirectories and files.
        abstract member Delete: correlationId: CorrelationId -> Task<GraceResult<string>>

        /// Validates incoming commands and converts them to events that are stored in the database.
        abstract member Handle: command: DirectoryVersionCommand -> eventMetadata: EventMetadata -> Task<GraceResult<string>>

    /// Defines the operations for the FileAppearance actor.
    [<Interface>]
    type IFileAppearanceActor =
        inherit IGrainWithStringKey

        /// Adds an appearance to the directory appearance list.
        abstract member Add: appearance: Appearance -> correlationId: CorrelationId -> Task

        /// Removes an appearance from the directory appearance list.
        abstract member Remove: appearance: Appearance -> correlationId: CorrelationId -> Task

        /// Checks if the directory appearance list contains the given appearance.
        abstract member Contains: appearance: Appearance -> correlationId: CorrelationId -> Task<bool>

        /// Returns the sorted set of appearances for this directory.
        abstract member Appearances: correlationId: CorrelationId -> Task<SortedSet<Appearance>>

    /// Defines the operations for the ReminderServiceLock actor.
    [<Interface>]
    type IGlobalLockActor =
        inherit IGrainWithStringKey

        /// Attempts to acquire a global lock for the Reminder Service. Returns true if the lock was acquired, otherwise false.
        abstract member AcquireLock: lockedBy: string -> Task<bool>

        /// Releases the global lock for the Reminder Service.
        abstract member ReleaseLock: releasedBy: string -> Task<Result<unit, string>>

        /// Returns true if the lock is currently held by any instance.
        abstract member IsLocked: unit -> Task<bool>

    ///Defines the operations for the Organization actor.
    [<Interface>]
    type IOrganizationActor =
        inherit IGraceReminderWithGuidKey

        /// Returns true if an organization with this ActorId already exists in the database.
        abstract member Exists: correlationId: CorrelationId -> Task<bool>

        /// Returns true if an organization with this ActorId has been deleted.
        abstract member IsDeleted: correlationId: CorrelationId -> Task<bool>

        /// Returns true if an repository with this name exists for this owner.
        abstract member RepositoryExists: repositoryName: RepositoryName -> correlationId: CorrelationId -> Task<bool>

        /// Returns the current state of the organization.
        abstract member Get: correlationId: CorrelationId -> Task<OrganizationDto>

        /// Returns a list of the repositories under this organization.
        abstract member ListRepositories: correlationId: CorrelationId -> Task<IReadOnlyDictionary<RepositoryId, RepositoryName>>

        /// Validates incoming commands and converts them to events that are stored in the database.
        abstract member Handle: command: OrganizationCommand -> eventMetadata: EventMetadata -> Task<GraceResult<string>>

    /// Defines the operations for the OrganizationName actor.
    [<Interface>]
    type IOrganizationNameActor =
        inherit IGrainWithStringKey

        /// Returns true if an organization with this organization name already exists in the database.
        abstract member SetOrganizationId: organizationId: OrganizationId -> correlationId: CorrelationId -> Task

        /// Returns the OrganizationId for the given OrganizationName.
        abstract member GetOrganizationId: correlationId: CorrelationId -> Task<OrganizationId option>

    /// Defines the operations for the Owner actor.
    [<Interface>]
    type IOwnerActor =
        inherit IGraceReminderWithGuidKey

        /// Returns true if an owner with this ActorId already exists in the database.
        abstract member Exists: correlationId: CorrelationId -> Task<bool>

        /// Returns true if an owner with this ActorId has been deleted.
        abstract member IsDeleted: correlationId: CorrelationId -> Task<bool>

        /// Returns true if an organization with this name exists for this owner.
        abstract member OrganizationExists: organizationName: string -> correlationId: CorrelationId -> Task<bool>

        /// Returns the current state of the owner.
        abstract member Get: correlationId: CorrelationId -> Task<OwnerDto>

        /// Returns a list of the organizations under this owner.
        abstract member ListOrganizations: correlationId: CorrelationId -> Task<IReadOnlyDictionary<OrganizationId, OrganizationName>>

        /// Validates incoming commands and converts them to events that are stored in the database.
        abstract member Handle: command: OwnerCommand -> eventMetadata: EventMetadata -> Task<GraceResult<string>>

    /// Defines the operations fpr the OwnerName actor.
    [<Interface>]
    type IOwnerNameActor =
        inherit IGrainWithStringKey
        /// Clears the OwnerId for the given OwnerName.
        abstract member ClearOwnerId: correlationId: CorrelationId -> Task

        /// Returns the OwnerId for the given OwnerName.
        abstract member GetOwnerId: correlationId: CorrelationId -> Task<OwnerId option>

        /// Sets the OwnerId for a given OwnerName.
        abstract member SetOwnerId: ownerId: OwnerId -> correlationId: CorrelationId -> Task

    /// Defines the operations for the Reference actor.
    [<Interface>]
    type IReferenceActor =
        inherit IGraceReminderWithGuidKey

        /// Returns true if the reference already exists in the database.
        abstract member Exists: correlationId: CorrelationId -> Task<bool>

        /// Returns the dto for this reference.
        abstract member Get: correlationId: CorrelationId -> Task<ReferenceDto>

        /// Returns the ReferenceType for this reference.
        abstract member GetReferenceType: correlationId: CorrelationId -> Task<ReferenceType>

        /// Validates incoming commands and converts them to events that are stored in the database.
        abstract member Handle: command: ReferenceCommand -> eventMetadata: EventMetadata -> Task<GraceResult<ReferenceDto>>

        /// Returns true if the reference has been deleted.
        abstract member IsDeleted: correlationId: CorrelationId -> Task<bool>

    [<Interface>]
    type IReminderActor =
        inherit IGrainWithGuidKey

        /// Creates a new reminder in the database.
        abstract member Create: reminder: ReminderDto -> correlationId: CorrelationId -> Task

        /// Deletes the reminder from the database.
        abstract member Delete: correlationId: CorrelationId -> Task

        /// Returns true if the reminder exists in the database.
        abstract member Exists: correlationId: CorrelationId -> Task<bool>

        /// Returns the reminder from the database.
        abstract member Get: correlationId: CorrelationId -> Task<ReminderDto>

        /// Sends the reminder to the source actor.
        abstract member Remind: correlationId: CorrelationId -> Task<Result<unit, GraceError>>

    /// Defines the operations for the PromotionGroup actor.
    [<Interface>]
    type IPromotionGroupActor =
        inherit IGraceReminderWithGuidKey

        /// Returns true if this promotion group already exists in the database.
        abstract member Exists: correlationId: CorrelationId -> Task<bool>

        /// Returns true if this promotion group has been deleted.
        abstract member IsDeleted: correlationId: CorrelationId -> Task<bool>

        /// Returns the current state of the promotion group.
        abstract member Get: correlationId: CorrelationId -> Task<PromotionGroupDto>

        /// Returns the list of events handled by this promotion group.
        abstract member GetEvents: correlationId: CorrelationId -> Task<IReadOnlyList<PromotionGroupEvent>>

        /// Validates incoming commands and converts them to events that are stored in the database.
        abstract member Handle: command: PromotionGroupCommand -> eventMetadata: EventMetadata -> Task<GraceResult<string>>

    /// Defines the operations for the Repository actor.
    [<Interface>]
    type IRepositoryActor =
        inherit IGraceReminderWithGuidKey

        /// Returns true if this actor already exists in the database, otherwise false.
        abstract member Exists: correlationId: CorrelationId -> Task<bool>

        /// Returns true if the repository has been created but is empty; otherwise false.
        abstract member IsEmpty: correlationId: CorrelationId -> Task<bool>

        /// Returns true if this repository has been deleted.
        abstract member IsDeleted: correlationId: CorrelationId -> Task<bool>

        /// Returns a record with the current state of the repository.
        abstract member Get: correlationId: CorrelationId -> Task<RepositoryDto>

        /// Returns the object storage provider for this repository.
        abstract member GetObjectStorageProvider: correlationId: CorrelationId -> Task<ObjectStorageProvider>

        /// Processes commands by checking that they're valid, and then converting them into events.
        abstract member Handle: command: RepositoryCommand -> eventMetadata: EventMetadata -> Task<GraceResult<string>>

    /// Defines the operations for the RepositoryName actor.
    [<Interface>]
    type IGrainRepositoryActor =
        inherit IGrainWithStringKey

        /// Sets the RepositoryId that matches the RepositoryName.
        abstract member SetRepositoryId: repositoryId: RepositoryId -> correlationId: CorrelationId -> Task

        /// Returns the RepositoryId for the given RepositoryName.
        abstract member GetRepositoryId: correlationId: CorrelationId -> Task<RepositoryId option>

    /// Defines the operations for the RepositoryName actor.
    [<Interface>]
    type IRepositoryNameActor =
        inherit IGrainWithStringKey

        /// Sets the RepositoryId that matches the RepositoryName.
        abstract member SetRepositoryId: repositoryId: RepositoryId -> correlationId: CorrelationId -> Task

        /// Returns the RepositoryId for the given RepositoryName.
        abstract member GetRepositoryId: correlationId: CorrelationId -> Task<RepositoryId option>
