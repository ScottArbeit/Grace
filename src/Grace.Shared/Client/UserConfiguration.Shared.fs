namespace Grace.Shared.Client

open Grace.Shared
open Grace.Shared.Utilities
open System
open System.IO
open System.Text.Json

module UserConfiguration =

    [<Literal>]
    let private DefaultMaxEntries = 5000

    [<Literal>]
    let private DefaultMaxFileBytes = 10L * 1024L * 1024L

    [<Literal>]
    let private DefaultRetentionDays = 90

    let defaultRedactOptionNames () =
        [|
            "token"
            "sas"
            "sig"
            "password"
            "connection-string"
            "connectionstring"
        |]

    let defaultRedactRegexes () =
        [|
            "(?i)(sig=)[^&]+"
            "(?i)(token=)[^&]+"
            "(?i)(password=)[^&]+"
        |]

    let defaultDestructiveTokenRegexes () =
        [|
            "(?i)\\b(delete|remove|purge|destroy|reset|drop)\\b"
        |]

    type HistoryConfiguration() =
        member val Enabled = false with get, set
        member val MaxEntries = DefaultMaxEntries with get, set
        member val MaxFileBytes = DefaultMaxFileBytes with get, set
        member val RetentionDays = DefaultRetentionDays with get, set
        member val RecordHistoryCommands = false with get, set
        member val RedactOptionNames = defaultRedactOptionNames () with get, set
        member val RedactRegexes = defaultRedactRegexes () with get, set
        member val DestructiveTokenRegexes = defaultDestructiveTokenRegexes () with get, set

    type AuthConfiguration() =
        member val ActiveAccountId = String.Empty with get, set
        member val ActiveTenantId = String.Empty with get, set
        member val ActiveUsername = String.Empty with get, set

    type UserConfiguration() =
        member val History = HistoryConfiguration() with get, set
        member val Auth = AuthConfiguration() with get, set

        override this.ToString() = serialize this

    type UserConfigurationLoadResult = { Configuration: UserConfiguration; WasCorrupt: bool; ErrorMessage: string option; CreatedNew: bool }

    let private normalizeHistory (history: HistoryConfiguration) =
        let normalized = if obj.ReferenceEquals(history, null) then HistoryConfiguration() else history

        if isNull normalized.RedactOptionNames then
            normalized.RedactOptionNames <- defaultRedactOptionNames ()

        if isNull normalized.RedactRegexes then
            normalized.RedactRegexes <- defaultRedactRegexes ()

        if isNull normalized.DestructiveTokenRegexes then
            normalized.DestructiveTokenRegexes <- defaultDestructiveTokenRegexes ()

        normalized

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

    let getUserGraceDirectory () =
        let userProfile = getUserProfileDirectory ()
        Path.Combine(userProfile, ".grace")

    let getUserConfigurationPath () =
        let userGraceDir = getUserGraceDirectory ()
        Path.Combine(userGraceDir, "userconfig.json")

    let ensureUserGraceDirectory () =
        let userGraceDir = getUserGraceDirectory ()
        Directory.CreateDirectory(userGraceDir) |> ignore
        userGraceDir

    let saveUserConfiguration (configuration: UserConfiguration) =
        try
            let path = getUserConfigurationPath ()
            ensureUserGraceDirectory () |> ignore
            let json = serialize (normalizeConfiguration configuration)
            File.WriteAllText(path, json)
            Ok()
        with
        | ex -> Error $"Failed to write user configuration: {ex.Message}"

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
