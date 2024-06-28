namespace Grace.Shared.Validation

open Grace.Shared.Utilities
open Grace.Shared.Resources.Text
open System

module Errors =

    module Branch =
        type BranchError =
            | AssignIsDisabled
            | BranchAlreadyExists
            | BranchDoesNotExist
            | BranchIdDoesNotExist
            | BranchIdIsRequired
            | BranchIsNotBasedOnLatestPromotion
            | BranchNameAlreadyExists
            | BranchNameIsRequired
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
            | ReferenceIdDoesNotExist
            | ReferenceTypeMustBeProvided
            | RepositoryDoesNotExist
            | SaveIsDisabled
            | Sha256HashDoesNotExist
            | Sha256HashIsRequired
            | StringIsTooLong
            | TagIsDisabled
            | ValueMustBePositive

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

    module Config =
        type ConfigError =
            | InvalidDirectoryPath

            static member getErrorMessage(configError: ConfigError) : string =
                match configError with
                | InvalidDirectoryPath -> getLocalizedString StringResourceName.InvalidDirectoryPath

            static member getErrorMessage(configError: ConfigError option) : string =
                match configError with
                | Some error -> ConfigError.getErrorMessage error
                | None -> String.Empty

    module Connect =
        type ConnectError =
            | RepositoryDoesNotExist
            | InvalidRepositoryId
            | InvalidRepositoryName
            | InvalidOwnerId
            | InvalidOwnerName
            | InvalidOrganizationId
            | InvalidOrganizationName

    module Diff =
        type DiffError =
            | DirectoryDoesNotExist
            | InvalidDirectoryId
            | InvalidOrganizationId
            | InvalidOrganizationName
            | InvalidOwnerId
            | InvalidOwnerName
            | InvalidRepositoryId
            | InvalidRepositoryName
            | InvalidSha256Hash
            | Sha256HashIsRequired

            static member getErrorMessage(diffError: DiffError) : string =
                match diffError with
                | DirectoryDoesNotExist -> getLocalizedString StringResourceName.DirectoryDoesNotExist
                | InvalidDirectoryId -> getLocalizedString StringResourceName.InvalidDirectoryId
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

    module DirectoryVersion =
        type DirectoryVersionError =
            | DirectoryAlreadyExists
            | DirectoryDoesNotExist
            | DirectorySha256HashAlreadyExists
            | FailedWhileApplyingEvent
            | FileNotFoundInObjectStorage
            | FileSha256HashDoesNotMatch
            | InvalidDirectoryId
            | InvalidRepositoryId
            | InvalidSha256Hash
            | InvalidSize
            | RelativePathMustNotBeEmpty
            | RepositoryDoesNotExist
            | Sha256HashIsRequired
            | Sha256HashDoesNotMatch

            static member getErrorMessage(directoryError: DirectoryVersionError) : string =
                match directoryError with
                | DirectoryAlreadyExists -> getLocalizedString StringResourceName.DirectoryAlreadyExists
                | DirectoryDoesNotExist -> getLocalizedString StringResourceName.DirectoryDoesNotExist
                | DirectorySha256HashAlreadyExists -> getLocalizedString StringResourceName.DirectorySha256HashAlreadyExists
                | FailedWhileApplyingEvent -> getLocalizedString StringResourceName.FailedWhileApplyingEvent
                | FileNotFoundInObjectStorage -> getLocalizedString StringResourceName.FileNotFoundInObjectStorage
                | FileSha256HashDoesNotMatch -> getLocalizedString StringResourceName.FileSha256HashDoesNotMatch
                | InvalidDirectoryId -> getLocalizedString StringResourceName.InvalidDirectoryId
                | InvalidRepositoryId -> getLocalizedString StringResourceName.InvalidRepositoryId
                | InvalidSha256Hash -> getLocalizedString StringResourceName.InvalidSha256Hash
                | InvalidSize -> getLocalizedString StringResourceName.InvalidSize
                | RelativePathMustNotBeEmpty -> getLocalizedString StringResourceName.RelativePathMustNotBeEmpty
                | RepositoryDoesNotExist -> getLocalizedString StringResourceName.RepositoryDoesNotExist
                | Sha256HashIsRequired -> getLocalizedString StringResourceName.Sha256HashIsRequired
                | Sha256HashDoesNotMatch -> getLocalizedString StringResourceName.Sha256HashDoesNotMatch

            static member getErrorMessage(directoryError: DirectoryVersionError option) : string =
                match directoryError with
                | Some error -> DirectoryVersionError.getErrorMessage error
                | None -> String.Empty

    module Owner =
        type OwnerError =
            | DeleteReasonIsRequired
            | DescriptionIsRequired
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

            static member getErrorMessage(ownerError: OwnerError) : string =
                match ownerError with
                | DeleteReasonIsRequired -> getLocalizedString StringResourceName.DeleteReasonIsRequired
                | DescriptionIsRequired -> getLocalizedString StringResourceName.DescriptionIsRequired
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

    module Organization =
        type OrganizationError =
            | DeleteReasonIsRequired
            | DescriptionIsRequired
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
            | OrganizationDescriptionIsRequired
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

            static member getErrorMessage(organizationError: OrganizationError) : string =
                match organizationError with
                | DeleteReasonIsRequired -> getLocalizedString StringResourceName.DeleteReasonIsRequired
                | DescriptionIsRequired -> getLocalizedString StringResourceName.DescriptionIsRequired
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
                | OrganizationDescriptionIsRequired -> getLocalizedString StringResourceName.DescriptionIsRequired
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

    module Reference =
        type ReferenceError =
            | AssignIsDisabled
            | BranchDoesNotExist
            | BranchIdDoesNotExist
            | BranchIsNotBasedOnLatestPromotion
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

            static member getErrorMessage(referenceError: ReferenceError) : string =
                match referenceError with
                | AssignIsDisabled -> getLocalizedString StringResourceName.AssignIsDisabled
                | BranchDoesNotExist -> getLocalizedString StringResourceName.BranchDoesNotExist
                | BranchIdDoesNotExist -> getLocalizedString StringResourceName.BranchIdDoesNotExist
                | BranchIsNotBasedOnLatestPromotion -> getLocalizedString StringResourceName.BranchIsNotBasedOnLatestPromotion
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

    module Repository =
        type RepositoryError =
            | BranchIdsAreRequired
            | DeleteReasonIsRequired
            | DescriptionIsRequired
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
            | InvalidDiffCacheDaysValue
            | InvalidDirectory
            | InvalidDirectoryVersionCacheDaysValue
            | InvalidMaxCountValue
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

            static member getErrorMessage(repositoryError: RepositoryError) : string =
                match repositoryError with
                | BranchIdsAreRequired -> getLocalizedString StringResourceName.BranchIdsAreRequired
                | DescriptionIsRequired -> getLocalizedString StringResourceName.DescriptionIsRequired
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
                | InvalidDiffCacheDaysValue -> getLocalizedString StringResourceName.InvalidDiffCacheDaysValue
                | InvalidDirectory -> getLocalizedString StringResourceName.InvalidDirectoryPath
                | InvalidDirectoryVersionCacheDaysValue -> getLocalizedString StringResourceName.InvalidDirectoryVersionCacheDaysValue
                | InvalidMaxCountValue -> getLocalizedString StringResourceName.InvalidMaxCountValue
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

    module Storage =
        type StorageError =
            | FailedCommunicatingWithObjectStorage
            | FailedToGetUploadUrls
            | FailedUploadingFilesToObjectStorage
            | FilesMustNotBeEmpty
            | NotImplemented
            | ObjectStorageException
            | UnknownObjectStorageProvider

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
