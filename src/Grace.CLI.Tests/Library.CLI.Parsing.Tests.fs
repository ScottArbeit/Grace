namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.SDK
open Grace.Types.Library
open NUnit.Framework
open System

/// Verifies the Library catalog and Windows synchronization tracer command surface.
[<Parallelizable(ParallelScope.All)>]
module LibraryCliParsingTests =

    /// Verifies `grace library` exposes the remote catalog operations and nested synchronization tracer.
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

        for synchronizationOperation in [| "enable"; "run"; "status" |] do
            GraceCommand
                .rootCommand
                .Parse(
                    [|
                        "library"
                        "sync"
                        synchronizationOperation
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

        GraceCommand
            .rootCommand
            .Parse(
                [| "library"; "sync"; "disable" |]
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

    /// Verifies root changes reject missing exact-version, path, or operation identity inputs during parsing.
    [<Test>]
    let ``library catalog changes require every concurrency input`` () =
        for arguments in
            [|
                [| "library"; "add"; "shared/docs" |]
                [|
                    "library"
                    "add"
                    "--expected-version"
                    Guid.NewGuid().ToString()
                |]
                [|
                    "library"
                    "remove"
                    "shared/docs"
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
