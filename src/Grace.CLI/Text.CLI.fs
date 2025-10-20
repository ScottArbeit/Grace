namespace Grace.CLI

open Grace.Shared.Resources.Utilities
open Grace.Shared.Resources.Text

module Text =

    module OptionName =
        [<Literal>]
        let AllowsLargeFiles = "--allows-large-files"

        [<Literal>]
        let AnonymousAccess = "--anonymous-access"

        [<Literal>]
        let BranchId = "--branch-id"

        [<Literal>]
        let BranchName = "--branch-name"

        [<Literal>]
        let CheckpointDays = "--checkpoint-days"

        [<Literal>]
        let CorrelationId = "--correlation-id"

        [<Literal>]
        let D1 = "--d1"

        [<Literal>]
        let D2 = "--d2"

        [<Literal>]
        let DefaultServerApiVersion = "--default-server-api-version"

        [<Literal>]
        let DeleteReason = "--delete-reason"

        [<Literal>]
        let Description = "--description"

        [<Literal>]
        let DiffCacheDays = "--diff-cache-days"

        [<Literal>]
        let Directory = "--directory"

        [<Literal>]
        let DirectoryVersionCacheDays = "--directory-version-cache-days"

        [<Literal>]
        let DirectoryVersionId = "--directory-version-id"

        [<Literal>]
        let DirectoryVersionId1 = "--directory-version-id-1"

        [<Literal>]
        let DirectoryVersionId2 = "--directory-version-id-2"

        [<Literal>]
        let DoNotSwitch = "--do-not-switch"

        [<Literal>]
        let Enabled = "--enabled"

        [<Literal>]
        let Force = "--force"

        [<Literal>]
        let ForceRecompute = "--force-recompute"

        [<Literal>]
        let FullSha = "--full-sha"

        [<Literal>]
        let GraceConfig = "--grace-config"

        [<Literal>]
        let IncludeDeleted = "--include-deleted"

        [<Literal>]
        let InitialPermissions = "--initial-permissions"

        [<Literal>]
        let ListDirectories = "--list-directories"

        [<Literal>]
        let ListFiles = "--list-files"

        [<Literal>]
        let LogicalDeleteDays = "--logical-delete-days"

        [<Literal>]
        let MaxCount = "--max-count"

        [<Literal>]
        let Message = "--message"

        [<Literal>]
        let NewName = "--new-name"

        [<Literal>]
        let OrganizationId = "--organization-id"

        [<Literal>]
        let OrganizationName = "--organization-name"

        [<Literal>]
        let OrganizationType = "--organization-type"

        [<Literal>]
        let Output = "--output"

        [<Literal>]
        let Overwrite = "--overwrite"

        [<Literal>]
        let OwnerId = "--owner-id"

        [<Literal>]
        let OwnerName = "--owner-name"

        [<Literal>]
        let OwnerType = "--owner-type"

        [<Literal>]
        let ParentBranchId = "--parent-branch-id"

        [<Literal>]
        let ParentBranchName = "--parent-branch-name"

        [<Literal>]
        let RecordSaves = "--record-saves"

        [<Literal>]
        let ReferenceId = "--reference-id"

        [<Literal>]
        let ReferenceType = "--reference-type"

        [<Literal>]
        let RepositoryId = "--repository-id"

        [<Literal>]
        let RepositoryName = "--repository-name"

        [<Literal>]
        let RetrieveDefaultBranch = "--retrieve-default-branch"

        [<Literal>]
        let SaveDays = "--save-days"

        [<Literal>]
        let SearchVisibility = "--search-visibility"

        [<Literal>]
        let ServerAddress = "--server-address"

        [<Literal>]
        let S1 = "--s1"

        [<Literal>]
        let S2 = "--s2"

        [<Literal>]
        let Sha256Hash = "--sha256-hash"

        [<Literal>]
        let Sha256Hash1 = "--sha256-hash-1"

        [<Literal>]
        let Sha256Hash2 = "--sha256-hash-2"

        [<Literal>]
        let ShowEvents = "--show-events"

        [<Literal>]
        let Status = "--status"

        [<Literal>]
        let Tag = "--tag"

        [<Literal>]
        let ToBranchId = "--to-branch-id"

        [<Literal>]
        let ToBranchName = "--to-branch-name"

        [<Literal>]
        let Visibility = "--visibility"

    /// The full list of strings that can be displayed to the user.
    type UIString =
        | CreatingNewDirectoryVersions
        | CreatingSaveReference
        | GettingCurrentBranch
        | GettingLatestVersion
        | ReadingGraceStatus
        | SavingDirectoryVersions
        | ScanningWorkingDirectory
        | UpdatingWorkingDirectory
        | UploadingFiles
        | WritingGraceStatusFile

        static member getString(uiString: UIString) : string =
            match uiString with
            | CreatingNewDirectoryVersions -> getLocalizedString StringResourceName.CreatingNewDirectoryVersions
            | CreatingSaveReference -> getLocalizedString StringResourceName.CreatingSaveReference
            | GettingCurrentBranch -> getLocalizedString StringResourceName.GettingCurrentBranch
            | GettingLatestVersion -> getLocalizedString StringResourceName.GettingLatestVersion
            | ReadingGraceStatus -> getLocalizedString StringResourceName.ReadingGraceStatus
            | SavingDirectoryVersions -> getLocalizedString StringResourceName.SavingDirectoryVersions
            | ScanningWorkingDirectory -> getLocalizedString StringResourceName.ScanningWorkingDirectory
            | UpdatingWorkingDirectory -> getLocalizedString StringResourceName.UpdatingWorkingDirectory
            | UploadingFiles -> getLocalizedString StringResourceName.UploadingFiles
            | WritingGraceStatusFile -> getLocalizedString StringResourceName.WritingGraceStatusFile
