namespace Grace.Cli
//////
open Grace.Shared
open Grace.Shared.Utilities
open NodaTime
open System.Globalization

module Log =

    type LogLevel =
        | Verbose
        | Informational
        | Error

    let Log (level: LogLevel) (message: string) =
        let pattern = "uuuu'-'MM'-'dd'T'HH':'mm':'ss.fff"
        printfn $"({getCurrentInstantExtended()} {Utilities.discriminatedUnionFullNameToString level} {message}"
        ()

    let LogInformational (message: string) = Log LogLevel.Informational message
    let LogError (message: string) = Log LogLevel.Error message
    let LogVerbose (message: string) = Log LogLevel.Verbose message
