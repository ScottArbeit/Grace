namespace Grace.Shared.Resources

module Text =

    /// This is intended to be the definitive list of locali[sz]ations that Grace supports.
    ///
    /// I'm starting with en-US, because that's all I know, but trying to do it right from the start.
    ///
    /// Sorry for all the upper-case; first letter of a discriminated union case has to be upper-case.
    type Language =
        | ``EN-US``

    type StringResourceName =
        | BranchAlreadyExists
        | BranchDoesNotExist
        | BranchIdIsRequired
        | BranchIdsAreRequired
        | BranchIsNotBasedOnLatestPromotion
        | BranchNameAlreadyExists
        | BranchNameIsRequired
        | CheckpointIsDisabled
        | CommitIsDisabled
        | DeleteReasonIsRequired
        | DescriptionIsRequired
        | DirectoryAlreadyExists
        | DirectoryDoesNotExist
        | DuplicateCorrelationId
        | EitherBranchIdOrBranchNameIsRequired
        | EitherOrganizationIdOrOrganizationNameIsRequired
        | EitherOwnerIdOrOwnerNameIsRequired
        | EitherRepositoryIdOrRepositoryNameIsRequired
        | FailedCommunicatingWithObjectStorage
        | FailedCreatingInitialBranch
        | FailedRebasingInitialBranch
        | FailedCreatingInitialPromotion
        | FailedToGetUploadUrls
        | FailedToRetrieveBranch
        | FailedUploadingFilesToObjectStorage
        | FailedWhileApplyingEvent
        | FailedWhileSavingEvent
        | FilesMustNotBeEmpty
        | GraceConfigFileNotFound
        | IndexFileNotFound
        | InitialPromotionMessage
        | InterprocessFileDeleted
        | InvalidBranchId
        | InvalidBranchName
        | InvalidCheckpointDaysValue
        | InvalidDirectoryPath
        | InvalidDirectoryId
        | InvalidMaxCountValue
        | InvalidObjectStorageProvider
        | InvalidOrganizationId
        | InvalidOrganizationName
        | InvalidOrganizationType
        | InvalidOwnerId
        | InvalidOwnerName
        | InvalidOwnerType
        | InvalidReferenceType
        | InvalidRepositoryId
        | InvalidRepositoryName
        | InvalidRepositoryStatus
        | InvalidSaveDaysValue
        | InvalidSearchVisibility
        | InvalidServerApiVersion
        | InvalidSha256Hash
        | InvalidSize
        | InvalidVisibilityValue
        | PromotionIsDisabled
        | PromotionNotAvailableBecauseThereAreNoPromotableReferences
        | MessageIsRequired
        | NotImplemented
        | ObjectCacheFileNotFound
        | ObjectStorageException
        | OrganizationAlreadyExists
        | OrganizationContainsRepositories
        | OrganizationDoesNotExist
        | OrganizationIdDoesNotExist
        | OrganizationIdIsRequired
        | OrganizationIsDeleted
        | OrganizationIsNotDeleted
        | OrganizationNameIsRequired
        | OrganizationTypeIsRequired
        | OwnerContainsOrganizations
        | OwnerDoesNotExist
        | OwnerIdDoesNotExist
        | OwnerIdAlreadyExists
        | OwnerIdIsRequired
        | OwnerIsDeleted
        | OwnerIsNotDeleted
        | OwnerNameAlreadyExists
        | OwnerNameIsRequired
        | OwnerTypeIsRequired
        | ParentBranchDoesNotExist
        | ReferenceIdDoesNotExist
        | ReferenceIdsAreRequired
        | ReferenceTypeMustBeProvided
        | RelativePathMustNotBeEmpty
        | RepositoryContainsBranches
        | RepositoryDoesNotExist
        | RepositoryIdAlreadyExists
        | RepositoryIdDoesNotExist
        | RepositoryIdIsRequired
        | RepositoryIsAlreadyInitialized
        | RepositoryIsDeleted
        | RepositoryIsNotDeleted
        | RepositoryIsNotEmpty
        | RepositoryNameIsRequired
        | SaveIsDisabled
        | SearchVisibilityIsRequired
        | ServerRequestsMustIncludeXCorrelationIdHeader
        | Sha256HashDoesNotExist
        | Sha256HashIsRequired
        | StringIsTooLong
        | TagIsDisabled
        | ValueMustBePositive