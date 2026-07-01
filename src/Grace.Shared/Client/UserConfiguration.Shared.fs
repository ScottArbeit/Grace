namespace Grace.Shared.Client

open Grace.Shared
open Grace.Shared.Utilities
open System
open System.IO
open System.Text.Json

/// Contains user configuration helpers.
module UserConfiguration =

    [<Literal>]
    let private DefaultMaxEntries = 5000

    [<Literal>]
    let private DefaultMaxFileBytes = 10L * 1024L * 1024L

    [<Literal>]
    let private DefaultRetentionDays = 90

    /// Lists option names whose values should be redacted in command history and diagnostics.
    let defaultRedactOptionNames () =
        [|
            "token"
            "sas"
            "sig"
            "password"
            "connection-string"
            "connectionstring"
        |]

    /// Lists regexes used to redact common secret values from persisted user data.
    let defaultRedactRegexes () =
        [|
            "(?i)(sig=)[^&]+"
            "(?i)(token=)[^&]+"
            "(?i)(password=)[^&]+"
        |]

    /// Lists regexes used to flag destructive command tokens for safer history handling.
    let defaultDestructiveTokenRegexes () =
        [|
            "(?i)\\b(delete|remove|purge|destroy|reset|drop)\\b"
        |]

    /// Represents history configuration.
    type HistoryConfiguration() =
        member val Enabled = false with get, set
        member val MaxEntries = DefaultMaxEntries with get, set
        member val MaxFileBytes = DefaultMaxFileBytes with get, set
        member val RetentionDays = DefaultRetentionDays with get, set
        member val RecordHistoryCommands = false with get, set
        member val RedactOptionNames = defaultRedactOptionNames () with get, set
        member val RedactRegexes = defaultRedactRegexes () with get, set
        member val DestructiveTokenRegexes = defaultDestructiveTokenRegexes () with get, set

    /// Represents auth configuration.
    type AuthConfiguration() =
        member val ActiveAccountId = String.Empty with get, set
        member val ActiveTenantId = String.Empty with get, set
        member val ActiveUsername = String.Empty with get, set

    /// Represents user configuration.
    type UserConfiguration() =
        member val History = HistoryConfiguration() with get, set
        member val Auth = AuthConfiguration() with get, set

        /// Returns the display representation for this value.
        override this.ToString() = serialize this

    /// Represents the user configuration load result contract.
    type UserConfigurationLoadResult = { Configuration: UserConfiguration; WasCorrupt: bool; ErrorMessage: string option; CreatedNew: bool }

    /// Represents the user configuration inspection contract.
    type UserConfigurationInspection = { Path: string; Exists: bool; Configuration: UserConfiguration option; ErrorMessage: string option }

    /// Normalizes history.
    let private normalizeHistory (history: HistoryConfiguration) =
        let normalized = if obj.ReferenceEquals(history, null) then HistoryConfiguration() else history

        if isNull normalized.RedactOptionNames then
            normalized.RedactOptionNames <- defaultRedactOptionNames ()

        if isNull normalized.RedactRegexes then
            normalized.RedactRegexes <- defaultRedactRegexes ()

        if isNull normalized.DestructiveTokenRegexes then
            normalized.DestructiveTokenRegexes <- defaultDestructiveTokenRegexes ()

        normalized

    /// Normalizes configuration.
    let private normalizeConfiguration (configuration: UserConfiguration) =
        let normalized =
            if obj.ReferenceEquals(configuration, null) then
                UserConfiguration()
            else
                configuration

        normalized.History <- normalizeHistory normalized.History

        normalized.Auth <-
            if obj.ReferenceEquals(normalized.Auth, null) then
                AuthConfiguration()
            else
                normalized.Auth

        normalized

    /// Gets user profile directory.
    let private getUserProfileDirectory () =
        let userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)

        if not <| String.IsNullOrWhiteSpace(userProfile) then
            userProfile
        else
            let home = Environment.GetEnvironmentVariable("HOME")

            if not <| String.IsNullOrWhiteSpace(home) then
                home
            else
                Environment.CurrentDirectory

    /// Gets user grace directory.
    let getUserGraceDirectory () =
        let userProfile = getUserProfileDirectory ()
        Path.Combine(userProfile, ".grace")

    /// Gets user configuration path.
    let getUserConfigurationPath () =
        let userGraceDir = getUserGraceDirectory ()
        Path.Combine(userGraceDir, "userconfig.json")

    /// Creates the per-user .grace directory when it is missing and returns its path.
    let ensureUserGraceDirectory () =
        let userGraceDir = getUserGraceDirectory ()
        Directory.CreateDirectory(userGraceDir) |> ignore
        userGraceDir

    /// Normalizes and writes the per-user Grace configuration JSON file.
    let saveUserConfiguration (configuration: UserConfiguration) =
        try
            let path = getUserConfigurationPath ()
            ensureUserGraceDirectory () |> ignore
            let json = serialize (normalizeConfiguration configuration)
            File.WriteAllText(path, json)
            Ok()
        with
        | ex -> Error $"Failed to write user configuration: {ex.Message}"

    /// Loads per-user Grace configuration, creating the default file when none exists.
    let loadUserConfiguration () =
        let defaultConfiguration = UserConfiguration()

        try
            ensureUserGraceDirectory () |> ignore
            let path = getUserConfigurationPath ()

            if not <| File.Exists(path) then
                let json = serialize defaultConfiguration
                File.WriteAllText(path, json)

                { Configuration = defaultConfiguration; WasCorrupt = false; ErrorMessage = None; CreatedNew = true }
            else
                let json = File.ReadAllText(path)

                let configuration = JsonSerializer.Deserialize<UserConfiguration>(json, Constants.JsonSerializerOptions)

                if obj.ReferenceEquals(configuration, null) then
                    {
                        Configuration = defaultConfiguration
                        WasCorrupt = true
                        ErrorMessage = Some "User configuration file is empty or invalid JSON."
                        CreatedNew = false
                    }
                else
                    { Configuration = normalizeConfiguration configuration; WasCorrupt = false; ErrorMessage = None; CreatedNew = false }
        with
        | ex ->
            {
                Configuration = defaultConfiguration
                WasCorrupt = true
                ErrorMessage = Some $"Failed to read user configuration: {ex.Message}"
                CreatedNew = false
            }

    /// Attempts to inspect user configuration.
    let tryInspectUserConfiguration () =
        let path = getUserConfigurationPath ()

        if not <| File.Exists(path) then
            { Path = path; Exists = false; Configuration = None; ErrorMessage = None }
        else
            try
                let json = File.ReadAllText(path)
                let configuration = JsonSerializer.Deserialize<UserConfiguration>(json, Constants.JsonSerializerOptions)

                if obj.ReferenceEquals(configuration, null) then
                    { Path = path; Exists = true; Configuration = None; ErrorMessage = Some "User configuration file is empty or invalid JSON." }
                else
                    { Path = path; Exists = true; Configuration = Some(normalizeConfiguration configuration); ErrorMessage = None }
            with
            | ex -> { Path = path; Exists = true; Configuration = None; ErrorMessage = Some $"Failed to read user configuration: {ex.Message}" }
