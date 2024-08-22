namespace Grace.Shared.Client

open Grace.Shared.Client.Theme
open Grace.Shared
open Grace.Shared.Types
open Grace.Shared.Utilities
open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Runtime.InteropServices

module Configuration =

    let writeNewConfiguration = false

    /// The global, client-side configuration object for Grace.
    // GraceConfiguration is implemented as a class, rather than a record, to allow for less brittle JSON serialization and deserialization of the configuration file.
    // Records don't handle missing values well during deserialization.
    type GraceConfiguration() =
        /// The OwnerId of the current repository.
        member val public OwnerId: OwnerId = OwnerId.Empty with get, set
        /// The OwnerName of the current repository.
        member val public OwnerName = OwnerName String.Empty with get, set
        /// The OrganizationId of the current repository.
        member val public OrganizationId: OrganizationId = OrganizationId.Empty with get, set
        /// The OrganizationName of the current repository.
        member val public OrganizationName = OrganizationName String.Empty with get, set
        /// The RepositoryId of the current repository.
        member val public RepositoryId: RepositoryId = RepositoryId.Empty with get, set
        /// The name of the current repository.
        member val public RepositoryName = RepositoryName String.Empty with get, set
        /// The BranchId of the current branch.
        member val public BranchId: BranchId = BranchId.Empty with get, set
        /// The BranchName of the current branch.
        member val public BranchName = BranchName String.Empty with get, set
        /// The name of the default branch in the current repository.
        member val public DefaultBranchName: BranchName = String.Empty with get, set
        /// The color themes available in Grace.
        member val public Themes = [| Theme.DefaultTheme |] with get, set
        /// The style of line endings that Grace should expect to handle for the current repository.
        member val public LineEndings = LineEndings.PlatformDependent.ToString() with get, set
        /// A list of branch names to prefetch whenever they have new commits.
        member val public Prefetch = [| String.Empty |] with get, set
        /// The local root directory of the current repository.
        member val public RootDirectory = Environment.CurrentDirectory with get, set
        /// The local root directory of the current repository, rendered with '/' as a path separator.
        member val public StandardizedRootDirectory = normalizeFilePath Environment.CurrentDirectory with get, set
        /// The Grace (/.grace) directory path in this repository.
        member val public GraceDirectory = Environment.CurrentDirectory with get, set
        /// The Grace objects (/.grace/objects) directory path. This is where Grace keeps locally-cached versions of repository artifacts.
        member val public ObjectDirectory = Environment.CurrentDirectory with get, set
        /// The location of the Grace index file.
        member val public GraceStatusFile = Constants.GraceStatusFileName with get, set
        /// The location of the Grace object cache file.
        member val public GraceObjectCacheFile = Constants.GraceObjectCacheFile with get, set
        /// The Grace objects directory cache path. This is where Grace keeps locally-cached DirectoryVersion's.
        member val public DirectoryVersionCache = Environment.CurrentDirectory with get, set
        /// The local directory where graceconfig.json is found for this repository.
        member val public ConfigurationDirectory = Environment.CurrentDirectory with get, set
        /// The blob storage provider used by this instance of Grace to store files.
        member val public ObjectStorageProvider = ObjectStorageProvider.Unknown with get, set
        /// The Uri of the instance of Grace Server used by the current repository.
        member val public ServerUri = @"http://127.0.0.1:5000" with get, set
        /// This version of Grace.
        member val public ProgramVersion = Constants.CurrentConfigurationVersion with get, set
        /// The current format of configuration.
        member val public ConfigurationVersion = String.Empty with get, set
        /// An OpenTelemetry ActivitySource for logging.
        //member val public ActivitySource: ActivitySource = new ActivitySource("Grace", "0.1") with get, set

        /// The current list of graceignore.json entries.
        [<JsonIgnore(Condition = JsonIgnoreCondition.Always)>]
        member val public GraceIgnoreEntries = [| String.Empty |] with get, set

        /// The current list of graceignore.json entries for files.
        [<JsonIgnore(Condition = JsonIgnoreCondition.Always)>]
        member val public GraceFileIgnoreEntries = [| String.Empty |] with get, set

        /// The current list of graceignore.json entries for directories.
        [<JsonIgnore(Condition = JsonIgnoreCondition.Always)>]
        member val public GraceDirectoryIgnoreEntries = [| String.Empty |] with get, set
        // /// The list of aliases for the Grace CLI.
        // member val public Aliases = Dictionary<string, string[]>() with get, set
        /// Indicates that this instance of GraceConfiguration has been populated.
        [<JsonIgnore(Condition = JsonIgnoreCondition.Always)>]
        member val public IsPopulated = false with get, set

        override this.ToString() = serialize this

    let mutable private graceConfiguration = GraceConfiguration()

    let saveConfigFile graceConfigurationFilePath (graceConfiguration: GraceConfiguration) =
        try
            let json = serialize graceConfiguration
            File.WriteAllText(graceConfigurationFilePath, json)
        with ex ->
            printfn $"Exception: {ex.Message}{Environment.NewLine}Stack trace: {ex.StackTrace}"

    let private findGraceConfigurationFile =
        try
            let mutable currentDirectory = DirectoryInfo(Environment.CurrentDirectory)

            if currentDirectory.FullName = Environment.SystemDirectory then
                // This happens when this is running from a Windows app, and we want to know where it is
                currentDirectory <- DirectoryInfo(Path.GetDirectoryName(Environment.ProcessPath))
            // Other ways I've tried to get the current directory:
            // let mutable currentDirectory = DirectoryInfo(Path.GetDirectoryName(Environment.ProcessPath))
            // let mutable currentDirectory = DirectoryInfo(Process.GetCurrentProcess().StartInfo.WorkingDirectory)
            let mutable graceConfigPath = String.Empty

            while String.IsNullOrEmpty(graceConfigPath) && not (isNull currentDirectory) do
                let fullPath = Path.Combine(currentDirectory.FullName, Constants.GraceConfigDirectory, Constants.GraceConfigFileName)
                //printfn $"Searching for configuration in {currentDirectory}..."
                if File.Exists(fullPath) then
                    graceConfigPath <- fullPath
                //printfn $"Found Grace configuration file at {fullPath}.{Environment.NewLine}{Constants.OutputDelimiter}"
                else
                    currentDirectory <- currentDirectory.Parent

            if not (String.IsNullOrEmpty(graceConfigPath)) then
                Ok graceConfigPath
            else
                //let graceConfigPath = Path.Combine(Environment.CurrentDirectory, Constants.GraceConfigDirectory, Constants.GraceConfigFileName)
                //Directory.CreateDirectory(FileInfo(graceConfigPath).DirectoryName) |> ignore
                //saveDefaultConfig graceConfigPath
                //Result.Ok graceConfigPath
                Error $"No {Constants.GraceConfigFileName} file found along current path. Please run `grace config write` to create one."

        with
        | :? System.IO.IOException as ex -> Error $"Exception while parsing directory paths: {ex.Message}"
        | ex -> Error $"Exception: {ex.Message}"

    let configurationFileExists () =
        match findGraceConfigurationFile with
        | Ok _ -> true
        | Error _ -> false

    let private parseConfigurationFile graceConfigurationFilePath =
        try
            // Read configuration into a stream from file path specified by graceConfigurationFilePath
            use stream = new FileStream(graceConfigurationFilePath, FileMode.Open, FileAccess.Read, FileShare.Read)
            // Read the stream into a buffer
            let buffer = Array.zeroCreate<byte> (int stream.Length)
            stream.Read(buffer, 0, buffer.Length) |> ignore
            // Deserialize the JSON configuration file into a GraceConfiguration object
            let graceConfiguration = JsonSerializer.Deserialize<GraceConfiguration>(buffer, Constants.JsonSerializerOptions)

            Ok graceConfiguration
        with ex ->
            Error $"Exception: {ex.Message}{Environment.NewLine}Stack trace: {ex.StackTrace}"

    let private getGraceIgnoreEntries graceIgnorePath =
        if File.Exists(graceIgnorePath) then
            File.ReadAllLines(graceIgnorePath)
            |> Seq.map (fun graceIgnoreLine ->
                let commentIndex = graceIgnoreLine.IndexOf('#')

                if commentIndex = -1 then
                    graceIgnoreLine.Trim()
                else
                    graceIgnoreLine.Substring(0, commentIndex).Trim())
            |> Seq.filter (fun graceIgnoreLine -> (not (String.IsNullOrEmpty(graceIgnoreLine))))
            |> Seq.map (fun graceIgnoreLine -> Path.TrimEndingDirectorySeparator(graceIgnoreLine))
            |> Seq.toArray
        else
            Array.empty

    let private getGraceConfiguration () =
        if graceConfiguration.IsPopulated then
            graceConfiguration
        else
            match findGraceConfigurationFile with
            | Ok graceConfigurationFilePath ->
#if DEBUG
                if writeNewConfiguration then
                    GraceConfiguration() |> saveConfigFile graceConfigurationFilePath
#endif
                let graceConfigurationDirectory = Path.GetDirectoryName(graceConfigurationFilePath)

                match (parseConfigurationFile graceConfigurationFilePath) with
                | Ok graceConfigurationFromFile ->
                    let graceIgnoreFullPath = (Path.Combine(graceConfigurationDirectory, Constants.GraceIgnoreFileName))

                    let graceIgnoreEntries = getGraceIgnoreEntries graceIgnoreFullPath

                    graceConfiguration <- graceConfigurationFromFile

                    graceConfiguration.RootDirectory <- Path.GetFullPath(Path.Combine(graceConfigurationDirectory, ".."))

                    graceConfiguration.GraceDirectory <- Path.GetFullPath(graceConfigurationDirectory)

                    graceConfiguration.ObjectDirectory <- Path.GetFullPath(Path.Combine(graceConfigurationDirectory, Constants.GraceObjectsDirectory))

                    graceConfiguration.GraceObjectCacheFile <- Path.Combine(graceConfiguration.ObjectDirectory, Constants.GraceObjectCacheFile)

                    graceConfiguration.GraceStatusFile <- Path.Combine(graceConfiguration.GraceDirectory, Constants.GraceStatusFileName)

                    graceConfiguration.DirectoryVersionCache <-
                        Path.GetFullPath(Path.Combine(graceConfigurationDirectory, Constants.GraceDirectoryVersionCacheName))

                    graceConfiguration.ConfigurationDirectory <- FileInfo(graceConfigurationFilePath).DirectoryName
                    //graceConfiguration.ActivitySource <- new ActivitySource("Grace", "0.1")
                    graceConfiguration.GraceIgnoreEntries <- graceIgnoreEntries

                    graceConfiguration.GraceFileIgnoreEntries <-
                        graceIgnoreEntries
                        |> Array.where (fun graceIgnoreLine -> not <| pathContainsSeparator graceIgnoreLine)

                    graceConfiguration.GraceDirectoryIgnoreEntries <-
                        graceIgnoreEntries
                        |> Array.where (fun graceIgnoreLine -> pathContainsSeparator graceIgnoreLine)
                    //graceConfiguration.Aliases <- aliases
                    graceConfiguration.IsPopulated <- true
                    graceConfiguration
                | Result.Error errorMessage ->
                    printfn $"{errorMessage}"
                    exit Results.InvalidConfigurationFile
            | Result.Error errorMessage ->
                // We didn't find a graceconfig.json file, so we'll create a default one in-memory just to finish the command.
                printfn $"{errorMessage}"
                logToConsole $"{Environment.StackTrace}"
                exit Results.ConfigurationFileNotFound

    // graceConfiguration <- GraceConfiguration()
    // graceConfiguration.IsPopulated <- true
    // graceConfiguration

    /// The current configuration of Grace in this repository.
    let Current () = getGraceConfiguration ()

    let resetConfiguration = graceConfiguration.IsPopulated <- false

    /// Saves the Grace configuration file after updates. Makes a backup of the previous version of the file.
    let updateConfiguration (newConfiguration: GraceConfiguration) =
        do
            File.Copy(
                Path.Combine(Current().ConfigurationDirectory, Constants.GraceConfigFileName),
                Path.Combine(Current().ConfigurationDirectory, $"{Constants.GraceConfigFileName}.backup"),
                overwrite = true
            )

        newConfiguration
        |> saveConfigFile (Path.Combine(Current().ConfigurationDirectory, Constants.GraceConfigFileName))

        graceConfiguration <- newConfiguration

    module Colors =
        let themes =
            if configurationFileExists () then
                Current().Themes
            else
                [| Theme.DefaultTheme |]

        let theme = themes[0]
        let Added = theme.DisplayColorOptions[DisplayColor.Added]
        let Deemphasized = theme.DisplayColorOptions[DisplayColor.Deemphasized]
        let Deleted = theme.DisplayColorOptions[DisplayColor.Deleted]
        let Changed = theme.DisplayColorOptions[DisplayColor.Changed]
        let Error = theme.DisplayColorOptions[DisplayColor.Error]
        let Important = theme.DisplayColorOptions[DisplayColor.Important]
        let Highlighted = theme.DisplayColorOptions[DisplayColor.Highlighted]
        let Verbose = theme.DisplayColorOptions[DisplayColor.Verbose]
