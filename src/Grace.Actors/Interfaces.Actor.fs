namespace Grace.Actors

open Dapr.Actors
open Grace.Actors.Commands
open Grace.Shared.Dto.Branch
open Grace.Shared.Dto.Diff
open Grace.Shared.Dto.Organization
open Grace.Shared.Dto.Owner
open Grace.Shared.Dto.Reference
open Grace.Shared.Dto.Repository
open Grace.Shared.Types
open Grace.Shared.Utilities
open NodaTime
open System.Collections.Generic
open System.Threading.Tasks

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

    /// This is an experimental interface to explore how to back up and rehydrate actor instances.
    [<Interface>]
    type IExportable<'T> =
        abstract member Export: unit -> Task<Result<List<'T>, ExportError>>
        abstract member Import: IReadOnlyList<'T> -> Task<Result<int, ImportError>>

    /// This is an experimental interface to explore how to implement important management functions for actors.
    [<Interface>]
    type IRevertable<'T> =
        abstract member EventCount: unit -> Task<int>
        abstract member RevertToInstant: Instant -> PersistAction -> Task<Result<'T, RevertError>>
        abstract member RevertBack: int -> PersistAction -> Task<Result<'T, RevertError>>

    /// Defines the operations for the Branch actor.
    [<Interface>]
    type IBranchActor =
        inherit IActor

        /// Validates incoming commands and converts them to events that are stored in the database.
        abstract member Handle: command: Branch.BranchCommand -> eventMetadata: EventMetadata -> Task<GraceResult<string>>

        /// Validates that a branch with this BranchId exists.
        abstract member Exists: correlationId: CorrelationId -> Task<bool>
        /// Retrieves the current state of the branch.
        abstract member Get: correlationId: CorrelationId -> Task<BranchDto>
        /// Retrieves the list of events handled by this branch.
        abstract member GetEvents: correlationId: CorrelationId -> Task<IReadOnlyList<Events.Branch.BranchEvent>>
        /// Retrieves the most recent commit from this branch.
        abstract member GetLatestCommit: correlationId: CorrelationId -> Task<ReferenceId>
        /// Retrieves the most recent promotion from this branch.
        abstract member GetLatestPromotion: correlationId: CorrelationId -> Task<ReferenceId>
        /// Retrieves the parent branch for a given branch.
        abstract member GetParentBranch: correlationId: CorrelationId -> Task<BranchDto>

    /// Defines the operations for the BranchName actor.
    [<Interface>]
    type IBranchNameActor =
        inherit IActor
        /// Returns the BranchId for the given BranchName.
        abstract member GetBranchId: correlationId: CorrelationId -> Task<BranchId option>
        /// Sets the BranchId that matches the BranchName.
        abstract member SetBranchId: branchId: BranchId -> correlationId: CorrelationId -> Task

    /// Defines the operations for the Commit actor.
    [<Interface>]
    type IContainerNameActor =
        inherit IActor
        /// Retrieves the container name for the RepositoryId specified for the actor.
        ///
        /// The container name is $"{ownerName}-{organizationName}-{repositoryName}".
        abstract member GetContainerName: correlationId: CorrelationId -> Task<Result<ContainerName, string>>

    /// Defines the operations for the Diff actor.
    [<Interface>]
    type IDiffActor =
        inherit IActor
        /// Populates the contents of the diff, without returning the results.
        abstract member Populate: correlationId: CorrelationId -> Task<bool>
        /// Gets the results of the diff.
        abstract member GetDiff: correlationId: CorrelationId -> Task<DiffDto>

    ///Defines the operations for the DirectoryVersion actor.
    [<Interface>]
    type IDirectoryVersionActor =
        inherit IActor
        /// Returns true if the actor instance already exists.
        abstract member Exists: correlationId: CorrelationId -> Task<bool>
        /// Returns the DirectoryVersion instance for this directory.
        abstract member Get: correlationId: CorrelationId -> Task<DirectoryVersion>
        /// Returns the list of subdirectories contained in this directory.
        abstract member GetCreatedAt: correlationId: CorrelationId -> Task<Instant>
        /// Returns the list of subdirectories contained in this directory.
        abstract member GetDirectories: correlationId: CorrelationId -> Task<List<DirectoryId>>
        /// Returns the list of files contained in this directory.
        abstract member GetFiles: correlationId: CorrelationId -> Task<List<FileVersion>>
        /// Returns the Sha256 hash value for this directory.
        abstract member GetSha256Hash: correlationId: CorrelationId -> Task<Sha256Hash>
        /// Returns the total size of files contained in this directory. This does not include files in subdirectories; for that, use GetSizeRecursive().
        abstract member GetSize: correlationId: CorrelationId -> Task<int64>

        /// Returns a list of DirectoryVersion objects for all subdirectories.
        abstract member GetDirectoryVersionsRecursive: forceRegenerate: bool -> correlationId: CorrelationId -> Task<List<DirectoryVersion>>

        /// Returns the total size of files contained in this directory and all subdirectories.
        abstract member GetRecursiveSize: correlationId: CorrelationId -> Task<int64>
        /// Delete the DirectoryVersion and all subdirectories and files.
        abstract member Delete: correlationId: CorrelationId -> Task<GraceResult<string>>

        /// Validates incoming commands and converts them to events that are stored in the database.
        abstract member Handle: command: DirectoryVersion.DirectoryVersionCommand -> eventMetadata: EventMetadata -> Task<GraceResult<string>>

    ///Defines the operations for the Organization actor.
    [<Interface>]
    type IOrganizationActor =
        inherit IActor
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
        abstract member Handle: command: Organization.OrganizationCommand -> eventMetadata: EventMetadata -> Task<GraceResult<string>>

    /// Defines the operations for the OrganizationName actor.
    [<Interface>]
    type IOrganizationNameActor =
        inherit IActor
        /// Returns true if an organization with this organization name already exists in the database.
        abstract member SetOrganizationId: organizationName: OrganizationName -> correlationId: CorrelationId -> Task
        /// Returns the OrganizationId for the given OrganizationName.
        abstract member GetOrganizationId: correlationId: CorrelationId -> Task<OrganizationId option>

    /// Defines the operations for the Owner actor.
    [<Interface>]
    type IOwnerActor =
        inherit IActor
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
        abstract member Handle: command: Owner.OwnerCommand -> eventMetadata: EventMetadata -> Task<GraceResult<string>>

    /// Defines the operations fpr the OwnerName actor.
    [<Interface>]
    type IOwnerNameActor =
        inherit IActor
        /// Sets the OwnerId for a given OwnerName.
        abstract member SetOwnerId: ownerName: OwnerName -> correlationId: CorrelationId -> Task
        /// Returns the OwnerId for the given OwnerName.
        abstract member GetOwnerId: correlationId: CorrelationId -> Task<OwnerId option>

    /// Defines the operations for the Reference actor.
    [<Interface>]
    type IReferenceActor =
        inherit IActor
        abstract member Exists: correlationId: CorrelationId -> Task<bool>
        abstract member Get: correlationId: CorrelationId -> Task<ReferenceDto>
        abstract member GetReferenceType: correlationId: CorrelationId -> Task<ReferenceType>

        abstract member Create:
            referenceId: ReferenceId *
            branchId: BranchId *
            directoryId: DirectoryId *
            sha256Hash: Sha256Hash *
            referenceType: ReferenceType *
            referenceText: ReferenceText ->
                correlationId: CorrelationId ->
                    Task<ReferenceDto>

        abstract member Delete: correlationId: CorrelationId -> Task<GraceResult<string>>

    /// Defines the operations for the Repository actor.
    [<Interface>]
    type IRepositoryActor =
        inherit IActor
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
        abstract member Handle: command: Repository.RepositoryCommand -> eventMetadata: EventMetadata -> Task<GraceResult<string>>

    /// Defines the operations for the RepositoryName actor.
    [<Interface>]
    type IRepositoryNameActor =
        inherit IActor
        /// Sets the RepositoryId that matches the RepositoryName.
        abstract member SetRepositoryId: repositoryName: RepositoryName -> correlationId: CorrelationId -> Task
        /// Returns the RepositoryId for the given RepositoryName.
        abstract member GetRepositoryId: correlationId: CorrelationId -> Task<string option>
