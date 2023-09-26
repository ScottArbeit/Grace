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

    type IExportable<'T> =
        //abstract member Export: unit -> Task<Result<IList<RepositoryEvent>, ExportError>>
        //abstract member Import: IList<RepositoryEvent> -> Task<Result<bool, ImportError>>
        abstract member Export: unit -> Task<Result<List<'T>, ExportError>>
        abstract member Import: IList<'T> -> Task<Result<int, ImportError>>

    type IRevertable<'T> =
        abstract member EventCount: unit -> Task<int>
        abstract member RevertToInstant: Instant -> PersistAction -> Task<Result<'T, RevertError>>
        abstract member RevertBack: int -> PersistAction -> Task<Result<'T, RevertError>>

    type IBranchActor =
        inherit IActor
        /// Validates incoming commands and converts them to events that are stored in the database.
        abstract member Handle : command: Branch.BranchCommand -> eventMetadata: EventMetadata -> Task<GraceResult<string>>
        /// Validates that a branch with this BranchId exists.
        abstract member Exists: unit -> Task<bool>
        /// Retrieves the current state of the branch.
        abstract member Get: unit -> Task<BranchDto>
        /// Retrieves the most recent commit from this branch.
        abstract member GetLatestCommit: unit -> Task<ReferenceId>
        /// Retrieves the most recent promotion from this branch.
        abstract member GetLatestPromotion: unit -> Task<ReferenceId>
        /// Retrieves the parent branch for a given branch.
        abstract member GetParentBranch: unit -> Task<BranchDto>

    type IBranchNameActor =
        inherit IActor
        /// Sets the BranchId that matches the BranchName.
        abstract member SetBranchId: branchName: string -> Task
        /// Returns the BranchId for the given BranchName.
        abstract member GetBranchId: unit -> Task<string option>

    type IContainerNameActor =
        inherit IActor
        /// Retrieves the container name for the RepositoryId specified for the actor.
        ///
        /// The container name is $"{ownerName}-{organizationName}-{repositoryName}".
        abstract member GetContainerName: unit -> Task<Result<ContainerName, string>>
    
    type IDiffActor = 
        inherit IActor
        /// Populates the contents of the diff.
        abstract member Populate: unit -> Task<bool>
        /// Gets the results of the diff.
        abstract member GetDiff: unit -> Task<DiffDto>

    type IDirectoryVersionActor =
        inherit IActor
        /// Returns true if the actor instance already exists.
        abstract member Exists: unit -> Task<bool>
        /// Returns the DirectoryVersion instance for this directory.
        abstract member Get: unit -> Task<DirectoryVersion>
        /// Returns the list of subdirectories contained in this directory.
        abstract member GetCreatedAt: unit -> Task<Instant>
        /// Returns the list of subdirectories contained in this directory.
        abstract member GetDirectories: unit -> Task<List<DirectoryId>>
        /// Returns the list of files contained in this directory.
        abstract member GetFiles: unit -> Task<List<FileVersion>>
        /// Returns the Sha256 hash value for this directory.
        abstract member GetSha256Hash: unit -> Task<Sha256Hash>
        /// Returns the total size of files contained in this directory. This does not include files in subdirectories; use GetSizeRecursive() for that value.
        abstract member GetSize: unit -> Task<uint64>
        /// Returns the total size of files contained in this directory and all subdirectories.
        abstract member GetSizeRecursive: unit -> Task<uint64>
        /// Returns a list of DirectoryVersion objects for all subdirectories.
        abstract member GetDirectoryVersionsRecursive: unit -> Task<List<DirectoryVersion>>
        /// Saves a DirectoryVersion instance as the state of this actor.
        abstract member Create: directoryContents: DirectoryVersion -> correlationId: string -> Task<GraceResult<string>>
        /// Delete the DirectoryVersion and all subdirectories and files.
        abstract member Delete: correlationId: string -> Task<GraceResult<string>>

    type IOrganizationActor =
        inherit IActor
        /// Returns true if an organization with this ActorId already exists in the database.
        abstract member Exists: unit -> Task<bool>
        /// Returns true if an organization with this ActorId has been deleted.
        abstract member IsDeleted: unit -> Task<bool>
        /// Returns true if an repository with this name exists for this owner.
        abstract member RepositoryExists: repositoryName: string -> Task<bool>
        /// Returns the current state of the organization.
        abstract member GetDto: unit -> Task<OrganizationDto>
        /// Returns a list of the repositories under this organization.
        abstract member ListRepositories: unit -> Task<IReadOnlyDictionary<RepositoryId, RepositoryName>>
        /// Validates incoming commands and converts them to events that are stored in the database.
        abstract member Handle: command: Organization.OrganizationCommand -> eventMetadata: EventMetadata -> Task<GraceResult<string>>

    type IOrganizationNameActor =
        inherit IActor
        /// Returns true if an organization with this organization name already exists in the database.
        abstract member SetOrganizationId: organizationName: string -> Task
        /// Returns the OrganizationId for the given OrganizationName.
        abstract member GetOrganizationId: unit -> Task<string option>

    type IOwnerActor =
        inherit IActor
        /// Returns true if an owner with this ActorId already exists in the database.
        abstract member Exists: unit -> Task<bool>
        /// Returns true if an owner with this ActorId has been deleted.
        abstract member IsDeleted: unit -> Task<bool>
        /// Returns true if an organization with this name exists for this owner.
        abstract member OrganizationExists: organizationName: string -> Task<bool>
        /// Returns the current state of the owner.
        abstract member GetDto: unit -> Task<OwnerDto>
        /// Returns a list of the organizations under this owner.
        abstract member ListOrganizations: unit -> Task<IReadOnlyDictionary<OrganizationId, OrganizationName>>
        /// Validates incoming commands and converts them to events that are stored in the database.
        abstract member Handle : command: Owner.OwnerCommand -> eventMetadata: EventMetadata -> Task<GraceResult<string>>

    type IOwnerNameActor =
        inherit IActor
        /// Sets the OwnerId for a given OwnerName.
        abstract member SetOwnerId: ownerName: string -> Task
        /// Returns the OwnerId for the given OwnerName.
        abstract member GetOwnerId: unit -> Task<string option>

    type IReferenceActor =
        inherit IActor
        abstract member Exists: unit -> Task<bool>
        abstract member Get: unit -> Task<ReferenceDto>
        abstract member GetReferenceType: unit -> Task<ReferenceType>
        abstract member Create: referenceId: ReferenceId 
                                * branchId: BranchId 
                                * directoryId: DirectoryId 
                                * sha256Hash: Sha256Hash
                                * referenceType: ReferenceType 
                                * referenceText: ReferenceText 
                                -> Task<ReferenceDto>
        abstract member Delete: correlationId: string -> Task<GraceResult<string>>

    type IRepositoryActor =
        inherit IActor
        /// Returns true if this actor already exists in the database, otherwise false.
        abstract member Exists: unit -> Task<bool>
        /// Returns true if the repository has been created but is empty; otherwise false.
        abstract member IsEmpty: unit -> Task<bool>
        /// Returns true if this repository has been deleted.
        abstract member IsDeleted: unit -> Task<bool>
        /// Returns a record with the current state of the repository.
        abstract member Get: unit -> Task<RepositoryDto>
        /// Returns the object storage provider for this repository.
        abstract member GetObjectStorageProvider: unit -> Task<ObjectStorageProvider>
        /// Processes commands by checking that they're valid, and then converting them into events.
        abstract member Handle : command: Repository.RepositoryCommand -> eventMetadata: EventMetadata -> Task<GraceResult<string>>

    type IRepositoryNameActor =
        inherit IActor
        /// Sets the RepositoryId that matches the RepositoryName.
        abstract member SetRepositoryId: repositoryName: string -> Task
        /// Returns the RepositoryId for the given RepositoryName.
        abstract member GetRepositoryId: unit -> Task<string option>
