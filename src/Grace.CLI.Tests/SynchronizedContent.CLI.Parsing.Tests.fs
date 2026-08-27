namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.SDK
open Grace.Types.SynchronizedContent
open NUnit.Framework
open System

/// Verifies the remote-only synchronized-content CLI and SDK surface accepted by Issue #1038.
[<Parallelizable(ParallelScope.All)>]
module SynchronizedContentCliParsingTests =

    /// Verifies both root command names expose exactly the four accepted remote root operations.
    [<Test>]
    let ``sync roots exposes only remote root operations`` () =
        for rootCommand in [| "sync"; "synchronize" |] do
            for operation in [| "get"; "list" |] do
                GraceCommand
                    .rootCommand
                    .Parse(
                        [| rootCommand; "roots"; operation |]
                    )
                    .Errors
                    .Count
                |> should equal 0

            for operation in [| "add"; "remove" |] do
                GraceCommand
                    .rootCommand
                    .Parse(
                        [|
                            rootCommand
                            "roots"
                            operation
                            "--root"
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
                    [| "sync"; unsupported |]
                )
                .Errors
                .Count
            |> should be (greaterThan 0)

    /// Verifies root mutations reject missing exact-version, path, or operation identity inputs during parsing.
    [<Test>]
    let ``sync root mutations require every concurrency input`` () =
        for arguments in
            [|
                [|
                    "sync"
                    "roots"
                    "add"
                    "--root"
                    "shared/docs"
                |]
                [|
                    "sync"
                    "roots"
                    "add"
                    "--expected-version"
                    Guid.NewGuid().ToString()
                |]
                [|
                    "sync"
                    "roots"
                    "remove"
                    "--root"
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
    let ``synchronized content SDK exposes complete remote contract`` () =
        let methods =
            typeof<SynchronizedContent>.GetMethods ()
            |> Array.filter (fun methodInfo -> methodInfo.DeclaringType = typeof<SynchronizedContent>)
            |> Array.map (fun methodInfo -> methodInfo.Name)
            |> Set.ofArray

        let expected =
            set [ "GetRoots"
                  "ListRoots"
                  "AddRoot"
                  "RemoveRoot"
                  "StartBootstrap"
                  "ContinueBootstrap"
                  "GetDeltas"
                  "SubmitMutation"
                  "GetOperation"
                  "PrepareContent"
                  "PrepareContentRead"
                  "DownloadContent"
                  "GetItem"
                  "GetNamespaceSlot"
                  "GetStatus" ]

        methods |> should equal expected

        typeof<SynchronizedContent>
            .GetMethod("ListRoots")
            .ReturnType.ToString()
            .Contains(nameof SynchronizedRootConfigurationDto)
        |> should equal true
