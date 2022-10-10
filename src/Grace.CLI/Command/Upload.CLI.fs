namespace Grace.Cli.Command

open Grace.Shared
open Spectre.Console
open Spectre.Console.Cli
open System
open System.ComponentModel
open System.IO

module Upload =

    type UploadSettings() =
        inherit CommandSettings()

    type Settings() = 
        inherit UploadSettings()

        [<CommandOption("--files|-f <FILE_NAMES>")>]
        [<Description("One or many files to upload; relative or absolute paths are fine.")>]
        member val public Files: string[] = Array.Empty() with get, set

        [<CommandOption("--showfullpath")>]
        [<Description("Show full file paths in output; default is false.")>]
        [<DefaultValue(false)>]
        member val public ShowFullPath: bool = false with get, set

        [<CommandOption("--verbose|-v")>]
        [<Description("Enables verbose output.")>]
        [<DefaultValue(false)>]
        member val public Verbose: bool = false with get, set

    type Command() =
        inherit Command<Settings>()

        /// Converts the input list of files to well-formed relative paths.
        let enumerateFiles filePaths =
            let fileNotFound = filePaths |> Seq.exists (fun f ->
                if not (File.Exists(f)) then
                    printfn $"File not found: {f}."
                    true
                else
                    false
                )

            if fileNotFound then
                Result.Error Results.FileNotFound
            else
                Result.Ok (filePaths |> Seq.map (fun filePath -> Path.GetRelativePath(".", filePath)))

        let getOutputFilePath filePath showFullPath =
            if showFullPath then
                Path.GetFullPath(filePath)
            else
                filePath

        interface ICommandLimiter<Settings>

        override this.Execute(context, settings) =
            if settings.Verbose then
                for file in settings.Files do
                    AnsiConsole.MarkupLine $"[blue]Will attempt file {file}...[/]"
            try
                match (enumerateFiles settings.Files) with
                    |  Ok filePaths ->
                        let fileCount = Seq.length filePaths
                        if fileCount = 1 then
                            AnsiConsole.MarkupLine $"[yellow]Uploading 1 file.[/]"
                            AnsiConsole.Write(new Rule())
                        else
                            AnsiConsole.MarkupLine $"[yellow]Uploading {fileCount} files.[/]"
                            AnsiConsole.Write(new Rule())

                        filePaths |> Seq.iter (fun filePath ->
                            //Copy file to .grace objects directory
                            //Call server to upload file
                            let outputFilePath = getOutputFilePath filePath (settings.ShowFullPath)
                            AnsiConsole.MarkupLine $"{outputFilePath}"
                            ()
                        )
                    
                        Results.Ok
                    | Error returnCode -> returnCode
            with ex ->
                AnsiConsole.MarkupLine $"[red]Exception:[/] {ex.Message}"
                AnsiConsole.MarkupLine $"[red]Stack trace:[/] {ex.StackTrace}"
                Results.Exception

        override this.Validate(context, settings) = 
            if settings.Files.Length = 0 then
                ValidationResult.Error "At least one file (--files) is required."
            else
                ValidationResult.Success()
