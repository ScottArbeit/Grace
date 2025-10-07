namespace Grace.CLI

open Grace.Shared.Resources.Utilities
open Grace.Shared.Resources.Text

module Text =

    module OptionName =
        [<Literal>]
        let OwnerId = "--owner-id"

        [<Literal>]
        let OwnerName = "--owner-name"

        [<Literal>]
        let OrganizationId = "--organization-id"

        [<Literal>]
        let OrganizationName = "--organization-name"

        [<Literal>]
        let RepositoryId = "--repository-id"

        [<Literal>]
        let RepositoryName = "--repository-name"

        [<Literal>]
        let BranchId = "--branch-id"

        [<Literal>]
        let BranchName = "--branch-name"

        [<Literal>]
        let NewName = "--new-name"

        [<Literal>]
        let Description = "--description"

        [<Literal>]
        let DoNotSwitch = "--do-not-switch"

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
