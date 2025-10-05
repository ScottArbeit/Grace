namespace Grace.CLI

open Grace.Shared.Resources.Utilities
open Grace.Shared.Resources.Text

module Text =

    module OptionName =
        let OwnerId = "--owner-id"
        let OwnerName = "--owner-name"
        let OrganizationId = "--organization-id"
        let OrganizationName = "--organization-name"
        let RepositoryId = "--repository-id"
        let RepositoryName = "--repository-name"
        let BranchId = "--branch-id"
        let BranchName = "--branch-name"
        let NewName = "--new-name"
        let Description = "--description"
        let DoNotSwitch = "--do-not-switch"

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
