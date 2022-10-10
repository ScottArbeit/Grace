namespace Grace.Cli.Command

open Grace.Shared
open Spectre.Console
open Spectre.Console.Cli
open System
open System.Collections.Generic
open System.ComponentModel
open System.IO

module Settings =
    type GraceSettings() =
        inherit CommandSettings()

    module Show =
        type Settings() =
            inherit GraceSettings()

            [<CommandOption("--settings <SETTINGS_NAMES>")>]
            [<Description("A list of specific setting names to show the value(s) for.")>]
            member val public Settings: string[] = Array.Empty() with get, set

        type Command() =
            inherit Command<Settings>()
            interface ICommandLimiter<GraceSettings>

            override this.Execute(context, settings) =
                0

            override this.Validate(context, settings) =
                ValidationResult.Success()

    module Update =
        type Settings() =
            inherit GraceSettings()

            [<CommandOption("--telemetryLevel <TELEMETRY_LEVEL>")>]
            [<Description("Sets the telemetry level for Grace; valid values are All | ErrorsOnly | None")>]
            [<DefaultValue("All")>]
            member val public TelemetryLevel: string = String.Empty with get, set

            [<CommandOption("--theme <THEME_NAME>")>]
            [<Description("Sets the color theme for Grace output.")>]
            [<DefaultValue("Default")>]
            member val public Theme: string = String.Empty with get, set

        type Command() =
            inherit Command<Settings>()
            interface ICommandLimiter<GraceSettings>

            override this.Execute(context, settings) =
                0

            override this.Validate(context, settings) =
                if not (String.IsNullOrEmpty(settings.TelemetryLevel)) then
                    match settings.TelemetryLevel with
                        | "All" | "ErrorsOnly" | "None" -> ValidationResult.Success()
                        | _ -> ValidationResult.Error("Blah")
                else
                    ValidationResult.Success()
