namespace Grace.CLI.Command

open Grace.Cache
open Grace.CLI.Common
open Grace.Shared
open Grace.Types.Common
open Spectre.Console
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks

/// Groups the repository-independent, read-only Grace Cache status command.
module CacheCommand =

    /// Holds the protected state root with a test-only override for isolated root-command proof.
    let mutable private stateRoot = CacheIdentity.StateRoot

    /// Redirects the inspected protected state root for serialized CLI tests.
    let internal setStateRootForTests root = stateRoot <- root

    /// Restores the fixed Product V1 protected state root after a serialized CLI test.
    let internal resetStateRootForTests () = stateRoot <- CacheIdentity.StateRoot

    /// Renders the approved redacted Cache status facts without serializing absent ready-only fields.
    let private renderStatus (parseResult: ParseResult) (status: CacheIdentityStatus) =
        if parseResult |> json then
            let value = JsonObject()
            value["Class"] <- JsonValue.Create(status.Class)
            value["Enrollment"] <- JsonValue.Create(status.Enrollment)
            value["Key"] <- JsonValue.Create(status.Key)

            status.CacheId
            |> Option.iter (fun cacheId -> value["CacheId"] <- JsonValue.Create(cacheId))

            status.Endpoint
            |> Option.iter (fun endpoint -> value["Endpoint"] <- JsonValue.Create(endpoint))

            status.BoundaryKind
            |> Option.iter (fun boundaryKind -> value["BoundaryKind"] <- JsonValue.Create(boundaryKind))

            status.RepositoryCount
            |> Option.iter (fun repositoryCount -> value["RepositoryCount"] <- JsonValue.Create(repositoryCount))

            GraceReturnValue.Create value (getCorrelationId parseResult)
            |> Ok
            |> renderOutput parseResult
        elif parseResult |> silent then
            0
        else
            let escape = Markup.Escape
            AnsiConsole.MarkupLine($"Class: {escape status.Class}")
            AnsiConsole.MarkupLine($"Enrollment: {escape status.Enrollment}")
            AnsiConsole.MarkupLine($"Key: {escape status.Key}")

            status.CacheId
            |> Option.iter (fun cacheId -> AnsiConsole.MarkupLine($"CacheId: {cacheId:D}"))

            status.Endpoint
            |> Option.iter (fun endpoint -> AnsiConsole.MarkupLine($"Endpoint: {escape endpoint}"))

            status.BoundaryKind
            |> Option.iter (fun boundaryKind -> AnsiConsole.MarkupLine($"BoundaryKind: {escape boundaryKind}"))

            status.RepositoryCount
            |> Option.iter (fun repositoryCount -> AnsiConsole.MarkupLine($"RepositoryCount: {repositoryCount}"))

            0

    /// Reads only protected local identity state and returns the Product V1 ready/non-ready exit code.
    let private statusHandler (parseResult: ParseResult) (cancellationToken: CancellationToken) =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let status = CacheIdentity.status stateRoot
            renderStatus parseResult status |> ignore
            return if status.Enrollment = "enrolled" then 0 else 1
        }

    /// Invokes the pure local Cache status command through System.CommandLine.
    type Status() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> = statusHandler parseResult cancellationToken

    /// Builds the repository-independent Cache command group.
    let Build =
        let cache = Command("cache", "Inspect a local Grace Cache identity.")
        let status = Command("status", "Report redacted local Grace Cache enrollment status.")
        status.Action <- Status()
        cache.Subcommands.Add(status)
        cache
