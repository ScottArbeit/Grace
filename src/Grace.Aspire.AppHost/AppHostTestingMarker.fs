namespace Projects

/// Identifies the executable F# AppHost assembly for Aspire's generic integration-test builder.
type Grace_Aspire_AppHost private () =

    /// Gets the AppHost source directory so testing can load the matching launch settings.
    static member ProjectPath = __SOURCE_DIRECTORY__
