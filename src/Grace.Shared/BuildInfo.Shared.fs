namespace Grace.Shared

open System
open System.Diagnostics
open System.IO
open System.Reflection

/// Contains build info helpers.
module BuildInfo =

    /// Represents the build identity contract.
    type BuildIdentity = { ProductVersion: string; FileVersion: string; InformationalVersion: string; AssemblyVersion: string; SourceRevisionId: string option }

    let private unknownVersion = "unknown"

    /// Normalizes blank build metadata fields to None.
    let private clean value =
        value
        |> Option.bind (fun text -> if String.IsNullOrWhiteSpace text then None else Some(text.Trim()))

    /// Selects the first non-blank metadata value from environment and assembly sources.
    let private firstNonEmpty values =
        values
        |> List.tryPick clean
        |> Option.defaultValue unknownVersion

    /// Attempts to source revision id.
    let private trySourceRevisionId informationalVersion =
        informationalVersion
        |> clean
        |> Option.bind (fun value ->
            let plusIndex = value.IndexOf('+')

            if plusIndex < 0 || plusIndex = value.Length - 1 then
                None
            else
                Some(value.Substring(plusIndex + 1)))

    /// Reads build metadata values from assembly attributes and normalized environment overrides.
    let createFromValues informationalVersion productVersion fileVersion assemblyVersion =
        let displayVersion =
            firstNonEmpty [ informationalVersion
                            productVersion
                            fileVersion
                            assemblyVersion ]

        {
            ProductVersion =
                firstNonEmpty [ productVersion
                                informationalVersion
                                fileVersion
                                assemblyVersion ]
            FileVersion =
                firstNonEmpty [ fileVersion
                                productVersion
                                informationalVersion
                                assemblyVersion ]
            InformationalVersion = displayVersion
            AssemblyVersion =
                firstNonEmpty [ assemblyVersion
                                fileVersion
                                productVersion
                                informationalVersion ]
            SourceRevisionId = trySourceRevisionId informationalVersion
        }

    /// Attempts to file version info.
    let private tryFileVersionInfo (assembly: Assembly) =
        try
            let location = assembly.Location

            if String.IsNullOrWhiteSpace location
               || not <| File.Exists location then
                None
            else
                Some(FileVersionInfo.GetVersionInfo location)
        with
        | _ -> None

    /// Reads build metadata from the supplied assembly attributes.
    let fromAssembly (assembly: Assembly) =
        try
            let informationalVersion =
                try
                    let attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()

                    if isNull attribute then None else Some attribute.InformationalVersion
                with
                | _ -> None

            let fileVersionInfo = tryFileVersionInfo assembly

            let productVersion =
                fileVersionInfo
                |> Option.bind (fun info -> Some info.ProductVersion)

            let fileVersion =
                fileVersionInfo
                |> Option.bind (fun info -> Some info.FileVersion)

            let assemblyVersion =
                try
                    let version = assembly.GetName().Version

                    if isNull version then None else Some(version.ToString())
                with
                | _ -> None

            createFromValues informationalVersion productVersion fileVersion assemblyVersion
        with
        | _ -> createFromValues None None None None

    /// Reads build metadata for the currently loaded Grace.Shared assembly.
    let current () =
        let assembly =
            try
                let entryAssembly = Assembly.GetEntryAssembly()

                if isNull entryAssembly then Assembly.GetExecutingAssembly() else entryAssembly
            with
            | _ -> Assembly.GetExecutingAssembly()

        fromAssembly assembly
