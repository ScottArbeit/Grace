namespace Grace.Shared.Resources

open Grace.Shared.Resources.en_US

module Utilities =

    /// Retrieves the localized version of a system resource string.
    ///
    /// Note: For now, it's hardcoded to return en_US. I'll fix this when we really implement localization.
    let getLocalizedString stringName = en_US.getString stringName
