namespace Grace.CLI

open Grace.Shared.Utilities
open Grace.Shared.Resources.Text

module Text =

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
