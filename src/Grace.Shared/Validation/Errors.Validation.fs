namespace Grace.Shared.Validation

open Grace.Shared.Resources.Text
open Grace.Shared.Resources.Utilities
open System

module Errors =

    // Marker interface + compile-time constraint
    type IErrorDiscriminatedUnion =
        interface
        end

    type BranchError =
        | AssignIsDisabled
        | BranchAlreadyExists
        | BranchDoesNotExist
        | BranchIdDoesNotExist
        | BranchIdIsRequired
        | BranchIsNotBasedOnLatestPromotion
        | BranchNameAlreadyExists
        | BranchNameIsRequired
        | CannotDeleteBranchesWithChildrenWithoutReassigningChildren
        | CannotSpecifyBothForceAndReassignChildBranches
        | CheckpointIsDisabled
        | CommitIsDisabled
        | DuplicateCorrelationId
        | EitherBranchIdOrBranchNameRequired
        | EitherDirectoryVersionIdOrSha256HashRequired
        | EitherOrganizationIdOrOrganizationNameRequired
        | EitherOwnerIdOrOwnerNameRequired
        | EitherRepositoryIdOrRepositoryNameIsRequired
        | EitherToBranchIdOrToBranchNameIsRequired
        | ExternalIsDisabled
        | FailedToAddReference
        | FailedToRetrieveBranch
        | FailedWhileApplyingEvent
        | IndexFileNotFound
        | InvalidBranchId
        | InvalidBranchName
        | InvalidOrganizationId
        | InvalidOrganizationName
        | InvalidOwnerId
        | InvalidOwnerName
        | InvalidReferenceId
        | InvalidReferenceType
        | InvalidRepositoryId
        | InvalidRepositoryName
        | InvalidSha256Hash
        | PromotionIsDisabled
        | PromotionNotAvailableBecauseThereAreNoPromotableReferences
        | MessageIsRequired
        | ObjectCacheFileNotFound
        | OrganizationDoesNotExist
        | OwnerDoesNotExist
        | ParentBranchDoesNotExist
        | ParentBranchDoesNotAllowPromotions
        | ReferenceIdDoesNotExist
        | ReferenceTypeMustBeProvided
        | RepositoryDoesNotExist
        | SaveIsDisabled
        | Sha256HashDoesNotExist
        | Sha256HashIsRequired
        | StringIsTooLong
        | TagIsDisabled
        | ValueMustBePositive

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(branchError: BranchError) : string =
            match branchError with
            | AssignIsDisabled -> getLocalizedString StringResourceName.AssignIsDisabled
            | BranchAlreadyExists -> getLocalizedString StringResourceName.BranchAlreadyExists
            | BranchDoesNotExist -> getLocalizedString StringResourceName.BranchDoesNotExist
            | BranchIdDoesNotExist -> getLocalizedString StringResourceName.BranchIdDoesNotExist
            | BranchIdIsRequired -> getLocalizedString StringResourceName.BranchIdIsRequired
            | BranchIsNotBasedOnLatestPromotion -> getLocalizedString StringResourceName.BranchIsNotBasedOnLatestPromotion
            | BranchNameAlreadyExists -> getLocalizedString StringResourceName.BranchNameAlreadyExists
            | BranchNameIsRequired -> getLocalizedString StringResourceName.BranchNameIsRequired
            | CannotDeleteBranchesWithChildrenWithoutReassigningChildren ->
                "You cannot delete a branch with children. Use --reassign-child-branches to reassign them to another parent, or --force to delete all child branches."
            | CannotSpecifyBothForceAndReassignChildBranches ->
                "You cannot specify both --force and --reassign-child-branches. Use --force to delete child branches, or --reassign-child-branches to move them to a new parent."
            | CheckpointIsDisabled -> getLocalizedString StringResourceName.CheckpointIsDisabled
            | CommitIsDisabled -> getLocalizedString StringResourceName.CommitIsDisabled
            | DuplicateCorrelationId -> getLocalizedString StringResourceName.DuplicateCorrelationId
            | EitherBranchIdOrBranchNameRequired -> getLocalizedString StringResourceName.EitherBranchIdOrBranchNameIsRequired
            | EitherDirectoryVersionIdOrSha256HashRequired -> getLocalizedString StringResourceName.EitherDirectoryVersionIdOrSha256HashRequired
            | EitherOrganizationIdOrOrganizationNameRequired -> getLocalizedString StringResourceName.EitherOrganizationIdOrOrganizationNameIsRequired
            | EitherOwnerIdOrOwnerNameRequired -> getLocalizedString StringResourceName.EitherOwnerIdOrOwnerNameIsRequired
            | EitherRepositoryIdOrRepositoryNameIsRequired -> getLocalizedString StringResourceName.EitherRepositoryIdOrRepositoryNameIsRequired
            | EitherToBranchIdOrToBranchNameIsRequired -> getLocalizedString StringResourceName.EitherToBranchIdOrToBranchNameIsRequired
            | ExternalIsDisabled -> getLocalizedString StringResourceName.ExternalIsDisabled
            | FailedToAddReference -> getLocalizedString StringResourceName.FailedToAddReference
            | FailedToRetrieveBranch -> getLocalizedString StringResourceName.FailedToRetrieveBranch
            | FailedWhileApplyingEvent -> getLocalizedString StringResourceName.FailedWhileApplyingEvent
            | IndexFileNotFound -> getLocalizedString StringResourceName.IndexFileNotFound
            | InvalidBranchId -> getLocalizedString StringResourceName.InvalidBranchId
            | InvalidBranchName -> getLocalizedString StringResourceName.InvalidBranchName
            | InvalidOrganizationId -> getLocalizedString StringResourceName.InvalidOrganizationId
            | InvalidOrganizationName -> getLocalizedString StringResourceName.InvalidOrganizationName
            | InvalidOwnerId -> getLocalizedString StringResourceName.InvalidOwnerId
            | InvalidOwnerName -> getLocalizedString StringResourceName.InvalidOwnerName
            | InvalidReferenceId -> getLocalizedString StringResourceName.InvalidReferenceId
            | InvalidReferenceType -> getLocalizedString StringResourceName.InvalidReferenceType
            | InvalidRepositoryId -> getLocalizedString StringResourceName.InvalidRepositoryId
            | InvalidRepositoryName -> getLocalizedString StringResourceName.InvalidRepositoryName
            | InvalidSha256Hash -> getLocalizedString StringResourceName.InvalidSha256Hash
            | PromotionIsDisabled -> getLocalizedString StringResourceName.PromotionIsDisabled
            | PromotionNotAvailableBecauseThereAreNoPromotableReferences ->
                getLocalizedString StringResourceName.PromotionNotAvailableBecauseThereAreNoPromotableReferences
            | MessageIsRequired -> getLocalizedString StringResourceName.MessageIsRequired
            | ObjectCacheFileNotFound -> getLocalizedString StringResourceName.ObjectCacheFileNotFound
            | OrganizationDoesNotExist -> getLocalizedString StringResourceName.OrganizationDoesNotExist
            | OwnerDoesNotExist -> getLocalizedString StringResourceName.OwnerDoesNotExist
            | ParentBranchDoesNotExist -> getLocalizedString StringResourceName.ParentBranchDoesNotExist
            | ParentBranchDoesNotAllowPromotions -> getLocalizedString StringResourceName.ParentBranchDoesNotAllowPromotions
            | ReferenceIdDoesNotExist -> getLocalizedString StringResourceName.ReferenceIdDoesNotExist
            | ReferenceTypeMustBeProvided -> getLocalizedString StringResourceName.ReferenceTypeMustBeProvided
            | RepositoryDoesNotExist -> getLocalizedString StringResourceName.RepositoryDoesNotExist
            | SaveIsDisabled -> getLocalizedString StringResourceName.SaveIsDisabled
            | Sha256HashDoesNotExist -> getLocalizedString StringResourceName.Sha256HashDoesNotExist
            | Sha256HashIsRequired -> getLocalizedString StringResourceName.Sha256HashIsRequired
            | StringIsTooLong -> getLocalizedString StringResourceName.StringIsTooLong
            | TagIsDisabled -> getLocalizedString StringResourceName.TagIsDisabled
            | ValueMustBePositive -> getLocalizedString StringResourceName.ValueMustBePositive

        static member getErrorMessage(branchError: BranchError option) : string =
            match branchError with
            | Some error -> BranchError.getErrorMessage error
            | None -> String.Empty

    type ConfigError =
        | InvalidDirectoryPath

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(configError: ConfigError) : string =
            match configError with
            | InvalidDirectoryPath -> getLocalizedString StringResourceName.InvalidDirectoryPath

        static member getErrorMessage(configError: ConfigError option) : string =
            match configError with
            | Some error -> ConfigError.getErrorMessage error
            | None -> String.Empty

    type ConnectError =
        | RepositoryDoesNotExist
        | InvalidRepositoryId
        | InvalidRepositoryName
        | InvalidOwnerId
        | InvalidOwnerName
        | InvalidOrganizationId
        | InvalidOrganizationName

    type DiffError =
        | DirectoryDoesNotExist
        | InvalidDirectoryVersionId
        | InvalidOrganizationId
        | InvalidOrganizationName
        | InvalidOwnerId
        | InvalidOwnerName
        | InvalidRepositoryId
        | InvalidRepositoryName
        | InvalidSha256Hash
        | Sha256HashIsRequired

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(diffError: DiffError) : string =
            match diffError with
            | DirectoryDoesNotExist -> getLocalizedString StringResourceName.DirectoryDoesNotExist
            | InvalidDirectoryVersionId -> getLocalizedString StringResourceName.InvalidDirectoryVersionId
            | InvalidOrganizationId -> getLocalizedString StringResourceName.InvalidOrganizationId
            | InvalidOrganizationName -> getLocalizedString StringResourceName.InvalidOrganizationName
            | InvalidOwnerId -> getLocalizedString StringResourceName.InvalidOwnerId
            | InvalidOwnerName -> getLocalizedString StringResourceName.InvalidOwnerName
            | InvalidRepositoryId -> getLocalizedString StringResourceName.InvalidRepositoryId
            | InvalidRepositoryName -> getLocalizedString StringResourceName.InvalidRepositoryName
            | InvalidSha256Hash -> getLocalizedString StringResourceName.InvalidSha256Hash
            | Sha256HashIsRequired -> getLocalizedString StringResourceName.Sha256HashIsRequired

        static member getErrorMessage(diffError: DiffError option) : string =
            match diffError with
            | Some error -> DiffError.getErrorMessage error
            | None -> String.Empty

    type DirectoryVersionError =
        | DirectoryAlreadyExists
        | DirectoryDoesNotExist
        | DirectorySha256HashAlreadyExists
        | FailedWhileApplyingEvent
        | FileNotFoundInObjectStorage
        | FileSha256HashDoesNotMatch
        | IndexFileNotFound
        | InvalidDirectoryVersionId
        | InvalidOrganizationId
        | InvalidOrganizationName
        | InvalidOwnerId
        | InvalidOwnerName
        | InvalidRepositoryId
        | InvalidRepositoryName
        | InvalidSha256Hash
        | InvalidSize
        | ObjectCacheFileNotFound
        | RelativePathMustNotBeEmpty
        | RepositoryDoesNotExist
        | Sha256HashIsRequired
        | Sha256HashDoesNotMatch

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(directoryError: DirectoryVersionError) : string =
            match directoryError with
            | DirectoryAlreadyExists -> getLocalizedString StringResourceName.DirectoryAlreadyExists
            | DirectoryDoesNotExist -> getLocalizedString StringResourceName.DirectoryDoesNotExist
            | DirectorySha256HashAlreadyExists -> getLocalizedString StringResourceName.DirectorySha256HashAlreadyExists
            | FailedWhileApplyingEvent -> getLocalizedString StringResourceName.FailedWhileApplyingEvent
            | FileNotFoundInObjectStorage -> getLocalizedString StringResourceName.FileNotFoundInObjectStorage
            | FileSha256HashDoesNotMatch -> getLocalizedString StringResourceName.FileSha256HashDoesNotMatch
            | IndexFileNotFound -> getLocalizedString StringResourceName.IndexFileNotFound
            | InvalidDirectoryVersionId -> getLocalizedString StringResourceName.InvalidDirectoryVersionId
            | InvalidOrganizationId -> getLocalizedString StringResourceName.InvalidOrganizationId
            | InvalidOrganizationName -> getLocalizedString StringResourceName.InvalidOrganizationName
            | InvalidOwnerId -> getLocalizedString StringResourceName.InvalidOwnerId
            | InvalidOwnerName -> getLocalizedString StringResourceName.InvalidOwnerName
            | InvalidRepositoryId -> getLocalizedString StringResourceName.InvalidRepositoryId
            | InvalidRepositoryName -> getLocalizedString StringResourceName.InvalidRepositoryName
            | InvalidSha256Hash -> getLocalizedString StringResourceName.InvalidSha256Hash
            | InvalidSize -> getLocalizedString StringResourceName.InvalidSize
            | ObjectCacheFileNotFound -> getLocalizedString StringResourceName.ObjectCacheFileNotFound
            | RelativePathMustNotBeEmpty -> getLocalizedString StringResourceName.RelativePathMustNotBeEmpty
            | RepositoryDoesNotExist -> getLocalizedString StringResourceName.RepositoryDoesNotExist
            | Sha256HashIsRequired -> getLocalizedString StringResourceName.Sha256HashIsRequired
            | Sha256HashDoesNotMatch -> getLocalizedString StringResourceName.Sha256HashDoesNotMatch

        static member getErrorMessage(directoryError: DirectoryVersionError option) : string =
            match directoryError with
            | Some error -> DirectoryVersionError.getErrorMessage error
            | None -> String.Empty

    type OwnerError =
        | DeleteReasonIsRequired
        | DescriptionIsRequired
        | DescriptionIsTooLong
        | DuplicateCorrelationId
        | EitherOwnerIdOrOwnerNameRequired
        | FailedWhileApplyingEvent
        | FailedWhileSavingEvent
        | InvalidOwnerId
        | InvalidOwnerName
        | InvalidOwnerType
        | InvalidSearchVisibility
        | OwnerContainsOrganizations
        | OwnerDoesNotExist
        | OwnerIdAlreadyExists
        | OwnerIdDoesNotExist
        | OwnerIdIsRequired
        | OwnerIsDeleted
        | OwnerIsNotDeleted
        | OwnerNameIsRequired
        | OwnerNameAlreadyExists
        | OwnerTypeIsRequired
        | SearchVisibilityIsRequired

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(ownerError: OwnerError) : string =
            match ownerError with
            | DeleteReasonIsRequired -> getLocalizedString StringResourceName.DeleteReasonIsRequired
            | DescriptionIsRequired -> getLocalizedString StringResourceName.DescriptionIsRequired
            | DescriptionIsTooLong -> getLocalizedString StringResourceName.DescriptionIsTooLong
            | DuplicateCorrelationId -> getLocalizedString StringResourceName.DuplicateCorrelationId
            | EitherOwnerIdOrOwnerNameRequired -> getLocalizedString StringResourceName.EitherOwnerIdOrOwnerNameIsRequired
            | FailedWhileApplyingEvent -> getLocalizedString StringResourceName.FailedWhileApplyingEvent
            | FailedWhileSavingEvent -> getLocalizedString StringResourceName.FailedWhileSavingEvent
            | InvalidOwnerId -> getLocalizedString StringResourceName.InvalidOwnerId
            | InvalidOwnerName -> getLocalizedString StringResourceName.InvalidOwnerName
            | InvalidOwnerType -> getLocalizedString StringResourceName.InvalidOwnerType
            | InvalidSearchVisibility -> getLocalizedString StringResourceName.InvalidSearchVisibility
            | OwnerContainsOrganizations -> getLocalizedString StringResourceName.OwnerContainsOrganizations
            | OwnerDoesNotExist -> getLocalizedString StringResourceName.OwnerDoesNotExist
            | OwnerIdAlreadyExists -> getLocalizedString StringResourceName.OwnerIdAlreadyExists
            | OwnerIdDoesNotExist -> getLocalizedString StringResourceName.OwnerIdDoesNotExist
            | OwnerIdIsRequired -> getLocalizedString StringResourceName.OwnerIdIsRequired
            | OwnerIsDeleted -> getLocalizedString StringResourceName.OwnerIsDeleted
            | OwnerIsNotDeleted -> getLocalizedString StringResourceName.OwnerIsNotDeleted
            | OwnerNameAlreadyExists -> getLocalizedString StringResourceName.OwnerNameAlreadyExists
            | OwnerNameIsRequired -> getLocalizedString StringResourceName.OwnerNameIsRequired
            | OwnerTypeIsRequired -> getLocalizedString StringResourceName.OwnerTypeIsRequired
            | SearchVisibilityIsRequired -> getLocalizedString StringResourceName.SearchVisibilityIsRequired

        static member getErrorMessage(ownerError: OwnerError option) : string =
            match ownerError with
            | Some error -> OwnerError.getErrorMessage error
            | None -> String.Empty

    type OrganizationError =
        | DeleteReasonIsRequired
        | DescriptionIsRequired
        | DescriptionIsTooLong
        | DuplicateCorrelationId
        | EitherOrganizationIdOrOrganizationNameRequired
        | EitherOwnerIdOrOwnerNameRequired
        | ExceptionCaught
        | FailedWhileApplyingEvent
        | FailedWhileSavingEvent
        | InvalidOrganizationId
        | InvalidOrganizationName
        | InvalidOrganizationType
        | InvalidOwnerId
        | InvalidOwnerName
        | InvalidRepositoryId
        | InvalidRepositoryName
        | InvalidSearchVisibility
        | OrganizationIdAlreadyExists
        | OrganizationNameAlreadyExists
        | OrganizationContainsRepositories
        | OrganizationDoesNotExist
        | OrganizationIdDoesNotExist
        | OrganizationIdIsRequired
        | OrganizationIsDeleted
        | OrganizationIsNotDeleted
        | OrganizationNameIsRequired
        | OrganizationTypeIsRequired
        | OwnerDoesNotExist
        | OwnerIdIsRequired
        | RepositoryNameIsRequired
        | SearchVisibilityIsRequired

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(organizationError: OrganizationError) : string =
            match organizationError with
            | DeleteReasonIsRequired -> getLocalizedString StringResourceName.DeleteReasonIsRequired
            | DescriptionIsRequired -> getLocalizedString StringResourceName.DescriptionIsRequired
            | DescriptionIsTooLong -> getLocalizedString StringResourceName.DescriptionIsTooLong
            | DuplicateCorrelationId -> getLocalizedString StringResourceName.DuplicateCorrelationId
            | EitherOrganizationIdOrOrganizationNameRequired -> getLocalizedString StringResourceName.EitherOrganizationIdOrOrganizationNameIsRequired
            | EitherOwnerIdOrOwnerNameRequired -> getLocalizedString StringResourceName.EitherOwnerIdOrOwnerNameIsRequired
            | ExceptionCaught -> getLocalizedString StringResourceName.ExceptionCaught
            | FailedWhileApplyingEvent -> getLocalizedString StringResourceName.FailedWhileApplyingEvent
            | FailedWhileSavingEvent -> getLocalizedString StringResourceName.FailedWhileSavingEvent
            | InvalidOrganizationId -> getLocalizedString StringResourceName.InvalidOrganizationId
            | InvalidOrganizationName -> getLocalizedString StringResourceName.InvalidOrganizationName
            | InvalidOrganizationType -> getLocalizedString StringResourceName.InvalidOrganizationType
            | InvalidOwnerId -> getLocalizedString StringResourceName.InvalidOwnerId
            | InvalidOwnerName -> getLocalizedString StringResourceName.InvalidOwnerName
            | InvalidRepositoryId -> getLocalizedString StringResourceName.InvalidRepositoryId
            | InvalidRepositoryName -> getLocalizedString StringResourceName.InvalidRepositoryName
            | InvalidSearchVisibility -> getLocalizedString StringResourceName.InvalidSearchVisibility
            | OrganizationIdAlreadyExists -> getLocalizedString StringResourceName.OrganizationIdAlreadyExists
            | OrganizationNameAlreadyExists -> getLocalizedString StringResourceName.OrganizationNameAlreadyExists
            | OrganizationContainsRepositories -> getLocalizedString StringResourceName.OrganizationContainsRepositories
            | OrganizationDoesNotExist -> getLocalizedString StringResourceName.OrganizationDoesNotExist
            | OrganizationIdIsRequired -> getLocalizedString StringResourceName.OrganizationIdIsRequired
            | OrganizationIdDoesNotExist -> getLocalizedString StringResourceName.OrganizationIdDoesNotExist
            | OrganizationIsDeleted -> getLocalizedString StringResourceName.OrganizationIsDeleted
            | OrganizationIsNotDeleted -> getLocalizedString StringResourceName.OrganizationIsNotDeleted
            | OrganizationNameIsRequired -> getLocalizedString StringResourceName.OrganizationNameIsRequired
            | OrganizationTypeIsRequired -> getLocalizedString StringResourceName.OrganizationTypeIsRequired
            | OwnerDoesNotExist -> getLocalizedString StringResourceName.OwnerDoesNotExist
            | OwnerIdIsRequired -> getLocalizedString StringResourceName.OwnerIdIsRequired
            | RepositoryNameIsRequired -> getLocalizedString StringResourceName.RepositoryNameIsRequired
            | SearchVisibilityIsRequired -> getLocalizedString StringResourceName.SearchVisibilityIsRequired

        static member getErrorMessage(organizationError: OrganizationError option) : string =
            match organizationError with
            | Some error -> OrganizationError.getErrorMessage error
            | None -> String.Empty

    type ReferenceError =
        | AssignIsDisabled
        | BranchDoesNotExist
        | BranchIdDoesNotExist
        | CheckpointIsDisabled
        | CommitIsDisabled
        | DuplicateCorrelationId
        | EitherBranchIdOrBranchNameRequired
        | EitherDirectoryVersionIdOrSha256HashRequired
        | EitherOrganizationIdOrOrganizationNameRequired
        | EitherOwnerIdOrOwnerNameRequired
        | EitherRepositoryIdOrRepositoryNameIsRequired
        | EitherToBranchIdOrToBranchNameIsRequired
        | ExternalIsDisabled
        | FailedToRetrieveBranch
        | FailedWhileApplyingEvent
        | IndexFileNotFound
        | InvalidBranchId
        | InvalidBranchName
        | InvalidOrganizationId
        | InvalidOrganizationName
        | InvalidOwnerId
        | InvalidOwnerName
        | InvalidReferenceId
        | InvalidReferenceType
        | InvalidRepositoryId
        | InvalidRepositoryName
        | InvalidSha256Hash
        | PromotionIsDisabled
        | PromotionNotAvailableBecauseThereAreNoPromotableReferences
        | MessageIsRequired
        | ObjectCacheFileNotFound
        | OrganizationDoesNotExist
        | OwnerDoesNotExist
        | ParentBranchDoesNotExist
        | ReferenceAlreadyExists
        | ReferenceIdDoesNotExist
        | ReferenceTypeMustBeProvided
        | RepositoryDoesNotExist
        | SaveIsDisabled
        | Sha256HashDoesNotExist
        | Sha256HashIsRequired
        | StringIsTooLong
        | TagIsDisabled
        | ValueMustBePositive

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(referenceError: ReferenceError) : string =
            match referenceError with
            | AssignIsDisabled -> getLocalizedString StringResourceName.AssignIsDisabled
            | BranchDoesNotExist -> getLocalizedString StringResourceName.BranchDoesNotExist
            | BranchIdDoesNotExist -> getLocalizedString StringResourceName.BranchIdDoesNotExist
            | CheckpointIsDisabled -> getLocalizedString StringResourceName.CheckpointIsDisabled
            | CommitIsDisabled -> getLocalizedString StringResourceName.CommitIsDisabled
            | DuplicateCorrelationId -> getLocalizedString StringResourceName.DuplicateCorrelationId
            | EitherBranchIdOrBranchNameRequired -> getLocalizedString StringResourceName.EitherBranchIdOrBranchNameIsRequired
            | EitherDirectoryVersionIdOrSha256HashRequired -> getLocalizedString StringResourceName.EitherDirectoryVersionIdOrSha256HashRequired
            | EitherOrganizationIdOrOrganizationNameRequired -> getLocalizedString StringResourceName.EitherOrganizationIdOrOrganizationNameIsRequired
            | EitherOwnerIdOrOwnerNameRequired -> getLocalizedString StringResourceName.EitherOwnerIdOrOwnerNameIsRequired
            | EitherRepositoryIdOrRepositoryNameIsRequired -> getLocalizedString StringResourceName.EitherRepositoryIdOrRepositoryNameIsRequired
            | EitherToBranchIdOrToBranchNameIsRequired -> getLocalizedString StringResourceName.EitherToBranchIdOrToBranchNameIsRequired
            | ExternalIsDisabled -> getLocalizedString StringResourceName.ExternalIsDisabled
            | FailedToRetrieveBranch -> getLocalizedString StringResourceName.FailedToRetrieveBranch
            | FailedWhileApplyingEvent -> getLocalizedString StringResourceName.FailedWhileApplyingEvent
            | IndexFileNotFound -> getLocalizedString StringResourceName.IndexFileNotFound
            | InvalidBranchId -> getLocalizedString StringResourceName.InvalidBranchId
            | InvalidBranchName -> getLocalizedString StringResourceName.InvalidBranchName
            | InvalidOrganizationId -> getLocalizedString StringResourceName.InvalidOrganizationId
            | InvalidOrganizationName -> getLocalizedString StringResourceName.InvalidOrganizationName
            | InvalidOwnerId -> getLocalizedString StringResourceName.InvalidOwnerId
            | InvalidOwnerName -> getLocalizedString StringResourceName.InvalidOwnerName
            | InvalidReferenceId -> getLocalizedString StringResourceName.InvalidReferenceId
            | InvalidReferenceType -> getLocalizedString StringResourceName.InvalidReferenceType
            | InvalidRepositoryId -> getLocalizedString StringResourceName.InvalidRepositoryId
            | InvalidRepositoryName -> getLocalizedString StringResourceName.InvalidRepositoryName
            | InvalidSha256Hash -> getLocalizedString StringResourceName.InvalidSha256Hash
            | PromotionIsDisabled -> getLocalizedString StringResourceName.PromotionIsDisabled
            | PromotionNotAvailableBecauseThereAreNoPromotableReferences ->
                getLocalizedString StringResourceName.PromotionNotAvailableBecauseThereAreNoPromotableReferences
            | MessageIsRequired -> getLocalizedString StringResourceName.MessageIsRequired
            | ObjectCacheFileNotFound -> getLocalizedString StringResourceName.ObjectCacheFileNotFound
            | OrganizationDoesNotExist -> getLocalizedString StringResourceName.OrganizationDoesNotExist
            | OwnerDoesNotExist -> getLocalizedString StringResourceName.OwnerDoesNotExist
            | ParentBranchDoesNotExist -> getLocalizedString StringResourceName.ParentBranchDoesNotExist
            | ReferenceAlreadyExists -> getLocalizedString StringResourceName.ReferenceAlreadyExists
            | ReferenceIdDoesNotExist -> getLocalizedString StringResourceName.ReferenceIdDoesNotExist
            | ReferenceTypeMustBeProvided -> getLocalizedString StringResourceName.ReferenceTypeMustBeProvided
            | RepositoryDoesNotExist -> getLocalizedString StringResourceName.RepositoryDoesNotExist
            | SaveIsDisabled -> getLocalizedString StringResourceName.SaveIsDisabled
            | Sha256HashDoesNotExist -> getLocalizedString StringResourceName.Sha256HashDoesNotExist
            | Sha256HashIsRequired -> getLocalizedString StringResourceName.Sha256HashIsRequired
            | StringIsTooLong -> getLocalizedString StringResourceName.StringIsTooLong
            | TagIsDisabled -> getLocalizedString StringResourceName.TagIsDisabled
            | ValueMustBePositive -> getLocalizedString StringResourceName.ValueMustBePositive

        static member getErrorMessage(branchError: ReferenceError option) : string =
            match branchError with
            | Some error -> ReferenceError.getErrorMessage error
            | None -> String.Empty

    type RepositoryError =
        | BranchIdsAreRequired
        | DeleteReasonIsRequired
        | DescriptionIsRequired
        | DescriptionIsTooLong
        | DuplicateCorrelationId
        | EitherOrganizationIdOrOrganizationNameRequired
        | EitherOwnerIdOrOwnerNameRequired
        | EitherRepositoryIdOrRepositoryNameRequired
        | FailedCreatingEmptyDirectoryVersion
        | FailedCreatingInitialBranch
        | FailedCreatingInitialPromotion
        | FailedRebasingInitialBranch
        | FailedWhileSavingEvent
        | FailedWhileApplyingEvent
        | InvalidCheckpointDaysValue
        | InvalidConflictResolutionPolicy
        | InvalidDiffCacheDaysValue
        | InvalidDirectory
        | InvalidDirectoryVersionCacheDaysValue
        | InvalidLogicalDeleteDaysValue
        | InvalidMaxCountValue
        | InvalidNewName
        | InvalidObjectStorageProvider
        | InvalidOrganizationId
        | InvalidOrganizationName
        | InvalidOwnerId
        | InvalidOwnerName
        | InvalidRepositoryId
        | InvalidRepositoryName
        | InvalidRepositoryStatus
        | InvalidSaveDaysValue
        | InvalidServerApiVersion
        | InvalidVisibilityValue
        | OrganizationIdIsRequired
        | OrganizationDoesNotExist
        | OwnerDoesNotExist
        | ReferenceIdsAreRequired
        | RepositoryContainsBranches
        | RepositoryDoesNotExist
        | RepositoryNameIsRequired
        | RepositoryIdDoesNotExist
        | RepositoryIdAlreadyExists
        | RepositoryIdIsRequired
        | RepositoryIsAlreadyInitialized
        | RepositoryIsDeleted
        | RepositoryIsNotDeleted
        | RepositoryIsNotEmpty
        | RepositoryNameAlreadyExists

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(repositoryError: RepositoryError) : string =
            match repositoryError with
            | BranchIdsAreRequired -> getLocalizedString StringResourceName.BranchIdsAreRequired
            | DescriptionIsRequired -> getLocalizedString StringResourceName.DescriptionIsRequired
            | DescriptionIsTooLong -> getLocalizedString StringResourceName.DescriptionIsTooLong
            | DeleteReasonIsRequired -> getLocalizedString StringResourceName.DeleteReasonIsRequired
            | DuplicateCorrelationId -> getLocalizedString StringResourceName.DuplicateCorrelationId
            | EitherOrganizationIdOrOrganizationNameRequired -> getLocalizedString StringResourceName.EitherOrganizationIdOrOrganizationNameIsRequired
            | EitherOwnerIdOrOwnerNameRequired -> getLocalizedString StringResourceName.EitherOwnerIdOrOwnerNameIsRequired
            | EitherRepositoryIdOrRepositoryNameRequired -> getLocalizedString StringResourceName.EitherRepositoryIdOrRepositoryNameIsRequired
            | FailedCreatingEmptyDirectoryVersion -> getLocalizedString StringResourceName.FailedCreatingEmptyDirectoryVersion
            | FailedCreatingInitialBranch -> getLocalizedString StringResourceName.FailedCreatingInitialBranch
            | FailedCreatingInitialPromotion -> getLocalizedString StringResourceName.FailedCreatingInitialPromotion
            | FailedRebasingInitialBranch -> getLocalizedString StringResourceName.FailedRebasingInitialBranch
            | FailedWhileSavingEvent -> getLocalizedString StringResourceName.FailedWhileSavingEvent
            | FailedWhileApplyingEvent -> getLocalizedString StringResourceName.FailedWhileApplyingEvent
            | InvalidCheckpointDaysValue -> getLocalizedString StringResourceName.InvalidCheckpointDaysValue
            | InvalidConflictResolutionPolicy -> getLocalizedString StringResourceName.InvalidConflictResolutionPolicy
            | InvalidDiffCacheDaysValue -> getLocalizedString StringResourceName.InvalidDiffCacheDaysValue
            | InvalidDirectory -> getLocalizedString StringResourceName.InvalidDirectoryPath
            | InvalidDirectoryVersionCacheDaysValue -> getLocalizedString StringResourceName.InvalidDirectoryVersionCacheDaysValue
            | InvalidLogicalDeleteDaysValue -> getLocalizedString StringResourceName.InvalidLogicalDeleteDaysValue
            | InvalidMaxCountValue -> getLocalizedString StringResourceName.InvalidMaxCountValue
            | InvalidNewName -> getLocalizedString StringResourceName.InvalidNewName
            | InvalidObjectStorageProvider -> getLocalizedString StringResourceName.InvalidObjectStorageProvider
            | InvalidOwnerId -> getLocalizedString StringResourceName.InvalidOwnerId
            | InvalidOrganizationId -> getLocalizedString StringResourceName.InvalidOrganizationId
            | InvalidOrganizationName -> getLocalizedString StringResourceName.InvalidOrganizationName
            | InvalidOwnerName -> getLocalizedString StringResourceName.InvalidOwnerName
            | InvalidRepositoryId -> getLocalizedString StringResourceName.InvalidRepositoryId
            | InvalidRepositoryName -> getLocalizedString StringResourceName.InvalidRepositoryName
            | InvalidRepositoryStatus -> getLocalizedString StringResourceName.InvalidRepositoryStatus
            | InvalidSaveDaysValue -> getLocalizedString StringResourceName.InvalidSaveDaysValue
            | InvalidServerApiVersion -> getLocalizedString StringResourceName.InvalidServerApiVersion
            | InvalidVisibilityValue -> getLocalizedString StringResourceName.InvalidVisibilityValue
            | OrganizationIdIsRequired -> getLocalizedString StringResourceName.OrganizationIdIsRequired
            | OrganizationDoesNotExist -> getLocalizedString StringResourceName.OrganizationDoesNotExist
            | OwnerDoesNotExist -> getLocalizedString StringResourceName.OwnerDoesNotExist
            | ReferenceIdsAreRequired -> getLocalizedString StringResourceName.ReferenceIdsAreRequired
            | RepositoryContainsBranches -> getLocalizedString StringResourceName.RepositoryContainsBranches
            | RepositoryDoesNotExist -> getLocalizedString StringResourceName.RepositoryDoesNotExist
            | RepositoryIdAlreadyExists -> getLocalizedString StringResourceName.RepositoryIdAlreadyExists
            | RepositoryIdDoesNotExist -> getLocalizedString StringResourceName.RepositoryIdDoesNotExist
            | RepositoryIdIsRequired -> getLocalizedString StringResourceName.RepositoryIdIsRequired
            | RepositoryIsAlreadyInitialized -> getLocalizedString StringResourceName.RepositoryIsAlreadyInitialized
            | RepositoryIsDeleted -> getLocalizedString StringResourceName.RepositoryIsDeleted
            | RepositoryIsNotDeleted -> getLocalizedString StringResourceName.RepositoryIsNotDeleted
            | RepositoryIsNotEmpty -> getLocalizedString StringResourceName.RepositoryIsNotEmpty
            | RepositoryNameIsRequired -> getLocalizedString StringResourceName.RepositoryNameIsRequired
            | RepositoryNameAlreadyExists -> getLocalizedString StringResourceName.RepositoryNameAlreadyExists


        static member getErrorMessage(repositoryError: RepositoryError option) : string =
            match repositoryError with
            | Some error -> RepositoryError.getErrorMessage error
            | None -> String.Empty

    type StorageError =
        | FailedCommunicatingWithObjectStorage
        | FailedToGetUploadUrls
        | FailedUploadingFilesToObjectStorage
        | FilesMustNotBeEmpty
        | NotImplemented
        | ObjectStorageException
        | UnknownObjectStorageProvider

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(storageError: StorageError) : string =
            match storageError with
            | FailedCommunicatingWithObjectStorage -> getLocalizedString StringResourceName.FailedCommunicatingWithObjectStorage
            | FailedToGetUploadUrls -> getLocalizedString StringResourceName.FailedToGetUploadUrls
            | FailedUploadingFilesToObjectStorage -> getLocalizedString StringResourceName.FailedUploadingFilesToObjectStorage
            | FilesMustNotBeEmpty -> getLocalizedString StringResourceName.FilesMustNotBeEmpty
            | NotImplemented -> getLocalizedString StringResourceName.NotImplemented
            | ObjectStorageException -> getLocalizedString StringResourceName.ObjectStorageException
            | UnknownObjectStorageProvider -> getLocalizedString StringResourceName.UnknownObjectStorageProvider

        static member getErrorMessage(storageError: StorageError option) : string =
            match storageError with
            | Some error -> StorageError.getErrorMessage error
            | None -> String.Empty

    type WorkItemError =
        | DuplicateCorrelationId
        | FailedWhileApplyingEvent
        | WorkItemAlreadyExists
        | WorkItemDoesNotExist
        | InvalidWorkItemId
        | InvalidWorkItemNumber
        | InvalidReferenceId
        | InvalidArtifactId
        | InvalidArtifactType
        | InvalidPromotionSetId
        | InvalidStatus

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(workItemError: WorkItemError) : string =
            match workItemError with
            | DuplicateCorrelationId -> "A command with this correlation ID has already been processed."
            | FailedWhileApplyingEvent -> "An error occurred while processing the work item event."
            | WorkItemAlreadyExists -> "A work item with this ID already exists."
            | WorkItemDoesNotExist -> "The specified work item does not exist."
            | InvalidWorkItemId -> "The work item ID is invalid."
            | InvalidWorkItemNumber -> "The work item number is invalid. Use a positive integer."
            | InvalidReferenceId -> "The reference ID is invalid."
            | InvalidArtifactId -> "The artifact ID is invalid."
            | InvalidArtifactType -> "The artifact type is invalid."
            | InvalidPromotionSetId -> "The promotion set ID is invalid."
            | InvalidStatus -> "The work item status is invalid."

        static member getErrorMessage(workItemError: WorkItemError option) : string =
            match workItemError with
            | Some error -> WorkItemError.getErrorMessage error
            | None -> String.Empty

    type PolicyError =
        | DuplicateCorrelationId
        | FailedWhileApplyingEvent
        | PolicySnapshotAlreadyExists
        | PolicySnapshotDoesNotExist
        | InvalidTargetBranchId
        | InvalidPolicySnapshotId

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(policyError: PolicyError) : string =
            match policyError with
            | DuplicateCorrelationId -> "A command with this correlation ID has already been processed."
            | FailedWhileApplyingEvent -> "An error occurred while processing the policy event."
            | PolicySnapshotAlreadyExists -> "The policy snapshot already exists."
            | PolicySnapshotDoesNotExist -> "The policy snapshot does not exist."
            | InvalidTargetBranchId -> "The target branch ID is invalid."
            | InvalidPolicySnapshotId -> "The policy snapshot ID is invalid."

        static member getErrorMessage(policyError: PolicyError option) : string =
            match policyError with
            | Some error -> PolicyError.getErrorMessage error
            | None -> String.Empty

    type ReviewError =
        | DuplicateCorrelationId
        | FailedWhileApplyingEvent
        | ReviewNotesDoesNotExist
        | FindingDoesNotExist
        | InvalidPromotionSetId
        | InvalidFindingId
        | InvalidReferenceId
        | InvalidPolicySnapshotId
        | InvalidResolutionState
        | InvalidChapterId

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(reviewError: ReviewError) : string =
            match reviewError with
            | DuplicateCorrelationId -> "A command with this correlation ID has already been processed."
            | FailedWhileApplyingEvent -> "An error occurred while processing the review event."
            | ReviewNotesDoesNotExist -> "The review notes do not exist."
            | FindingDoesNotExist -> "The specified finding does not exist."
            | InvalidPromotionSetId -> "The promotion set ID is invalid."
            | InvalidFindingId -> "The finding ID is invalid."
            | InvalidReferenceId -> "The reference ID is invalid."
            | InvalidPolicySnapshotId -> "The policy snapshot ID is invalid."
            | InvalidResolutionState -> "The resolution state is invalid."
            | InvalidChapterId -> "The chapter ID is invalid."

        static member getErrorMessage(reviewError: ReviewError option) : string =
            match reviewError with
            | Some error -> ReviewError.getErrorMessage error
            | None -> String.Empty

    type QueueError =
        | DuplicateCorrelationId
        | FailedWhileApplyingEvent
        | QueueAlreadyInitialized
        | QueueNotInitialized
        | PromotionSetNotInQueue
        | InvalidTargetBranchId
        | InvalidPromotionSetId
        | InvalidPolicySnapshotId

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(queueError: QueueError) : string =
            match queueError with
            | DuplicateCorrelationId -> "A command with this correlation ID has already been processed."
            | FailedWhileApplyingEvent -> "An error occurred while processing the queue event."
            | QueueAlreadyInitialized -> "The promotion queue is already initialized."
            | QueueNotInitialized -> "The promotion queue has not been initialized."
            | PromotionSetNotInQueue -> "The specified promotion set is not in the queue."
            | InvalidTargetBranchId -> "The target branch ID is invalid."
            | InvalidPromotionSetId -> "The promotion set ID is invalid."
            | InvalidPolicySnapshotId -> "The policy snapshot ID is invalid."

        static member getErrorMessage(queueError: QueueError option) : string =
            match queueError with
            | Some error -> QueueError.getErrorMessage error
            | None -> String.Empty

    type ValidationSetError =
        | DuplicateCorrelationId
        | FailedWhileApplyingEvent
        | ValidationSetAlreadyExists
        | ValidationSetDoesNotExist
        | InvalidValidationSetId
        | InvalidTargetBranchId
        | ValidationSetRulesRequired
        | ValidationRuleEventNamesRequired
        | InvalidValidationRuleEventName
        | ValidationDefinitionsRequired

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(validationSetError: ValidationSetError) : string =
            match validationSetError with
            | DuplicateCorrelationId -> "A command with this correlation ID has already been processed."
            | FailedWhileApplyingEvent -> "An error occurred while processing the validation set event."
            | ValidationSetAlreadyExists -> "A validation set with this ID already exists."
            | ValidationSetDoesNotExist -> "The validation set does not exist."
            | InvalidValidationSetId -> "The validation set ID is invalid."
            | InvalidTargetBranchId -> "The target branch ID is invalid."
            | ValidationSetRulesRequired -> "A validation set must include at least one rule."
            | ValidationRuleEventNamesRequired -> "Each validation rule must include at least one canonical event name."
            | InvalidValidationRuleEventName -> "Validation rules may only reference published canonical event names."
            | ValidationDefinitionsRequired -> "A validation set must include at least one validation definition."

        static member getErrorMessage(validationSetError: ValidationSetError option) : string =
            match validationSetError with
            | Some error -> ValidationSetError.getErrorMessage error
            | None -> String.Empty

    type ValidationResultError =
        | DuplicateCorrelationId
        | FailedWhileApplyingEvent
        | InvalidValidationResultId
        | InvalidValidationSetId
        | InvalidPromotionSetId
        | InvalidPromotionSetStepId
        | InvalidValidationStatus
        | ValidationNameRequired
        | ValidationVersionRequired
        | InvalidArtifactId
        | StepsComputationAttemptRequired
        | InvalidStepsComputationAttempt

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(validationResultError: ValidationResultError) : string =
            match validationResultError with
            | DuplicateCorrelationId -> "A command with this correlation ID has already been processed."
            | FailedWhileApplyingEvent -> "An error occurred while processing the validation result event."
            | InvalidValidationResultId -> "The validation result ID is invalid."
            | InvalidValidationSetId -> "The validation set ID is invalid."
            | InvalidPromotionSetId -> "The promotion set ID is invalid."
            | InvalidPromotionSetStepId -> "The promotion set step ID is invalid."
            | InvalidValidationStatus -> "The validation status is invalid."
            | ValidationNameRequired -> "ValidationName is required."
            | ValidationVersionRequired -> "ValidationVersion is required."
            | InvalidArtifactId -> "One or more artifact IDs are invalid."
            | StepsComputationAttemptRequired -> "StepsComputationAttempt is required when PromotionSetId is provided."
            | InvalidStepsComputationAttempt -> "StepsComputationAttempt must be greater than zero."

        static member getErrorMessage(validationResultError: ValidationResultError option) : string =
            match validationResultError with
            | Some error -> ValidationResultError.getErrorMessage error
            | None -> String.Empty

    type ArtifactError =
        | DuplicateCorrelationId
        | FailedWhileApplyingEvent
        | ArtifactAlreadyExists
        | ArtifactDoesNotExist
        | InvalidArtifactId
        | InvalidArtifactType
        | InvalidMimeType
        | InvalidSize

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(artifactError: ArtifactError) : string =
            match artifactError with
            | DuplicateCorrelationId -> "A command with this correlation ID has already been processed."
            | FailedWhileApplyingEvent -> "An error occurred while processing the artifact event."
            | ArtifactAlreadyExists -> "The artifact already exists."
            | ArtifactDoesNotExist -> "The artifact does not exist."
            | InvalidArtifactId -> "The artifact ID is invalid."
            | InvalidArtifactType -> "The artifact type is invalid."
            | InvalidMimeType -> "The mime type is invalid."
            | InvalidSize -> "Artifact size must be zero or greater."

        static member getErrorMessage(artifactError: ArtifactError option) : string =
            match artifactError with
            | Some error -> ArtifactError.getErrorMessage error
            | None -> String.Empty

    type TestError =
        | TestFailed

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(testError: TestError) : string =
            match testError with
            | TestFailed -> getLocalizedString StringResourceName.TestFailed

        static member getErrorMessage(testError: TestError option) : string =
            match testError with
            | Some error -> TestError.getErrorMessage error
            | None -> String.Empty

    type ReminderError =
        | InvalidReminderDuration
        | InvalidReminderTime
        | InvalidReminderType
        | ReminderActorIdIsRequired
        | ReminderActorNameIsRequired
        | ReminderDoesNotExist
        | ReminderIdIsRequired

        interface IErrorDiscriminatedUnion

        static member getErrorMessage(reminderError: ReminderError) : string =
            match reminderError with
            | InvalidReminderDuration -> "Invalid reminder duration. Use formats like '+15m', '+1h', '+1d'."
            | InvalidReminderTime -> "Invalid reminder time. Use ISO8601 format."
            | InvalidReminderType -> "Invalid reminder type. Valid types: Maintenance, PhysicalDeletion, DeleteCachedState, DeleteZipFile."
            | ReminderActorIdIsRequired -> "Actor ID is required for creating a reminder."
            | ReminderActorNameIsRequired -> "Actor name is required for creating a reminder."
            | ReminderDoesNotExist -> "The specified reminder does not exist."
            | ReminderIdIsRequired -> "Reminder ID is required."

        static member getErrorMessage(reminderError: ReminderError option) : string =
            match reminderError with
            | Some error -> ReminderError.getErrorMessage error
            | None -> String.Empty

    /// Given an error object, returns the corresponding error message string.
    let getErrorMessage<'T when 'T :> IErrorDiscriminatedUnion> (error: 'T) : string =
        match box error with
        | :? BranchError as branchError -> BranchError.getErrorMessage branchError
        | :? ConfigError as configError -> ConfigError.getErrorMessage configError
        | :? DiffError as diffError -> DiffError.getErrorMessage diffError
        | :? DirectoryVersionError as directoryVersionError -> DirectoryVersionError.getErrorMessage directoryVersionError
        | :? OwnerError as ownerError -> OwnerError.getErrorMessage ownerError
        | :? OrganizationError as organizationError -> OrganizationError.getErrorMessage organizationError
        | :? ReferenceError as referenceError -> ReferenceError.getErrorMessage referenceError
        | :? ReminderError as reminderError -> ReminderError.getErrorMessage reminderError
        | :? RepositoryError as repositoryError -> RepositoryError.getErrorMessage repositoryError
        | :? StorageError as storageError -> StorageError.getErrorMessage storageError
        | :? TestError as testError -> TestError.getErrorMessage testError
        | :? WorkItemError as workItemError -> WorkItemError.getErrorMessage workItemError
        | :? PolicyError as policyError -> PolicyError.getErrorMessage policyError
        | :? ReviewError as reviewError -> ReviewError.getErrorMessage reviewError
        | :? QueueError as queueError -> QueueError.getErrorMessage queueError
        | :? ValidationSetError as validationSetError -> ValidationSetError.getErrorMessage validationSetError
        | :? ValidationResultError as validationResultError -> ValidationResultError.getErrorMessage validationResultError
        | :? ArtifactError as artifactError -> ArtifactError.getErrorMessage artifactError
        | _ -> String.Empty

    /// Given an optional error object, returns the corresponding error message string, or an empty string if None.
    let getErrorOptionMessage<'T when 'T :> IErrorDiscriminatedUnion> (error: 'T option) : string =
        match error with
        | Some err -> getErrorMessage err
        | None -> String.Empty
