namespace Grace.CLI

open Grace.Shared
open Grace.Types.Common
open Grace.Shared.Utilities
open NodaTime
open System
open System.Globalization
open System.Collections.Concurrent

/// Groups the log command parser, handlers, and output helpers.
module Log =

    /// Models log level values passed between the parser and log handlers.
    type LogLevel =
        | Verbose
        | Informational
        | Error

    /// Configures CLI logging so command output stays separate from diagnostic events.
    let Log (level: LogLevel) (message: string) =
        let pattern = "uuuu'-'MM'-'dd'T'HH':'mm':'ss.fff"
        printfn $"({getCurrentInstantExtended ()} {Utilities.getDiscriminatedUnionFullName level} {message}"
        ()

    /// Configures CLI logging so command output stays separate from diagnostic events.
    let LogInformational (message: string) = Log LogLevel.Informational message
    /// Configures CLI logging so command output stays separate from diagnostic events.
    let LogError (message: string) = Log LogLevel.Error message
    /// Configures CLI logging so command output stays separate from diagnostic events.
    let LogVerbose (message: string) = Log LogLevel.Verbose message
