namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.SDK
open Grace.Types.Library
open NUnit.Framework
open System

/// Verifies Library catalog and synchronization command parsing without invoking runtime state.
[<Parallelizable(ParallelScope.All)>]
module LibraryCliParsingTests =

    /// Accepts only the explicit file and same-parent name gesture, without caller-supplied operation or version identities.
    [<Test>]
    let ``library rename accepts two positional arguments and rejects internal identities`` () =
        let args =
            [|
                "library"
                "rename"
                "Library/nested/file.txt"
                "new.txt"
            |]

        Assert.That(GraceCommand.rootCommand.Parse(args).Errors.Count, Is.Zero)

        Assert.That(
            GraceCommand
                .rootCommand
                .Parse(
                    [|
                        "library"
                        "rename"
                        "Library/file.txt"
                    |]
                )
                .Errors
                .Count,
            Is.GreaterThan(0)
        )

        for option in
            [|
                "--operation-id"
                "--expected-version"
                "--item-id"
                "--repository-id"
            |] do
            Assert.That(
                GraceCommand
                    .rootCommand
                    .Parse(
                        Array.append args [| option; string (Guid.NewGuid()) |]
                    )
                    .Errors
                    .Count,
                Is.GreaterThan(0)
            )

    /// Keeps synchronization confined to participation, finite run, explicit pause/resume and status.
    [<Test>]
    let ``library synchronization accepts exact verbs and repository locators`` () =
        for verb in
            [
                "enable"
                "run"
                "pause"
                "resume"
                "status"
            ] do
            let parsed =
                GraceCommand.rootCommand.Parse [| "library"
                                                  "sync"
                                                  verb
                                                  "--repository-id"
                                                  "a140fd79-c198-4f9d-8d73-76a7f5fb3649"
                                                  "--output"
                                                  "Json" |]

            parsed.Errors.Count |> should equal 0

        for verb in
            [
                "disable"
                "offline"
                "repair"
                "re-enable"
            ] do
            GraceCommand
                .rootCommand
                .Parse(
                    [| "library"; "sync"; verb |]
                )
                .Errors
                .Count
            |> should be (greaterThan 0)

    /// Verifies `grace library` exposes exactly the four accepted remote catalog operations.
    [<Test>]
    let ``library exposes only remote catalog operations`` () =
        GraceCommand
            .rootCommand
            .Parse(
                [| "library"; "list" |]
            )
            .Errors
            .Count
        |> should equal 0

        GraceCommand
            .rootCommand
            .Parse(
                [| "library"; "get"; "shared/docs" |]
            )
            .Errors
            .Count
        |> should equal 0

        for operation in [| "add"; "remove" |] do
            GraceCommand
                .rootCommand
                .Parse(
                    [|
                        "library"
                        operation
                        "shared/docs"
                        "--expected-version"
                        "43d7030c-d212-4307-948d-fb83b67b1c82"
                        "--operation-id"
                        "cb89957a-33c7-4ac2-b55c-0ea2553571de"
                    |]
                )
                .Errors
                .Count
            |> should equal 0

        for unsupported in
            [|
                "enable"
                "disable"
                "run"
                "status"
            |] do
            GraceCommand
                .rootCommand
                .Parse(
                    [| "library"; unsupported |]
                )
                .Errors
                .Count
            |> should be (greaterThan 0)

        for staleAlias in [| "sync"; "synchronize"; "libraries" |] do
            GraceCommand
                .rootCommand
                .Parse(
                    [| staleAlias; "list" |]
                )
                .Errors
                .Count
            |> should be (greaterThan 0)

    /// Verifies root changes still require a path while concurrency options can be omitted.
    [<Test>]
    let ``library catalog changes require a path`` () =
        for arguments in
            [|
                [| "library"; "add" |]
                [|
                    "library"
                    "add"
                    "--expected-version"
                    Guid.NewGuid().ToString()
                |]
                [|
                    "library"
                    "remove"
                    "--expected-version"
                    Guid.NewGuid().ToString()
                |]
            |] do
            GraceCommand
                .rootCommand
                .Parse(
                    arguments
                )
                .Errors
                .Count
            |> should be (greaterThan 0)

    /// Allows ordinary commands with only their path and keeps malformed or missing option values as parser errors.
    [<TestCase("add")>]
    [<TestCase("remove")>]
    let ``catalog command optional GUID values remain distinguishable from invalid input`` verb =
        let arguments = [| "library"; verb; "shared/docs" |]

        GraceCommand
            .rootCommand
            .Parse(
                arguments
            )
            .Errors
            .Count
        |> should equal 0

        for option in
            [|
                "--expected-version"
                "--operation-id"
            |] do
            for values in
                [|
                    [| option |]
                    [| option; "not-a-guid" |]
                |] do
                GraceCommand
                    .rootCommand
                    .Parse(
                        Array.append arguments values
                    )
                    .Errors
                    .Count
                |> should be (greaterThan 0)

    /// Verifies the typed SDK exposes every accepted remote route without local participation commands.
    [<Test>]
    let ``Libraries SDK exposes complete remote contract`` () =
        let methods =
            typeof<Libraries>.GetMethods ()
            |> Array.filter (fun methodInfo -> methodInfo.DeclaringType = typeof<Libraries>)
            |> Array.map (fun methodInfo -> methodInfo.Name)
            |> Set.ofArray

        let expected =
            set [ "GetCatalog"
                  "ListLibraries"
                  "AddLibrary"
                  "RemoveLibrary"
                  "StartBootstrap"
                  "ContinueBootstrap"
                  "GetChanges"
                  "SubmitChange"
                  "GetOperation"
                  "PrepareContent"
                  "PrepareContentRead"
                  "DownloadContent"
                  "GetItem"
                  "GetNamespaceSlot"
                  "GetStatus" ]

        methods |> should equal expected

        typeof<Libraries>
            .GetMethod("ListLibraries")
            .ReturnType.ToString()
            .Contains(nameof LibraryCatalogDto)
        |> should equal true
