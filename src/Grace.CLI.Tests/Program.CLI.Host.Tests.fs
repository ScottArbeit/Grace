namespace Grace.CLI.Tests

open Grace.CLI
open NUnit.Framework
open Spectre.Console
open System
open System.CommandLine
open System.CommandLine.Invocation
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

/// Covers deterministic host dispatch behavior shared by the production CLI root graph.
[<TestFixture>]
[<NonParallelizable>]
module ProgramCliHostTests =

    /// Runs a supplied token-aware status action through System.CommandLine.
    type private StatusAction(handler: CancellationToken -> Task<int>) =
        inherit AsynchronousCommandLineAction()

        /// Dispatches the test status action with the token supplied by the root host.
        override _.InvokeAsync(_: ParseResult, cancellationToken: CancellationToken) = handler cancellationToken

    /// Directs Spectre.Console to the supplied writer while root invocation output is captured.
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    /// Builds the controlled Cache group used to exercise the production root graph.
    let private createCacheCommand (handler: CancellationToken -> Task<int>) =
        let cache = Command("cache", "Inspect a local Grace Cache identity.")
        let status = Command("status", "Report redacted local Grace Cache enrollment status.")
        status.Action <- StatusAction(handler)
        cache.Subcommands.Add(status)
        cache

    /// Builds the internal dependencies used by focused host tests.
    let private dependencies (initializer: unit -> unit) (handler: CancellationToken -> Task<int>) : GraceCommand.RootDependencies =
        { CreateCacheCommand = (fun () -> createCacheCommand handler); InitializeExecution = initializer }

    /// Invokes the production root construction and run path while capturing root output.
    let private runWithCapturedOutput (dependencies: GraceCommand.RootDependencies) (args: string array) (cancellationToken: CancellationToken) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer

            let exitCode =
                GraceCommand.run dependencies args cancellationToken
                |> fun invocation -> invocation.GetAwaiter().GetResult()

            exitCode, writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    /// Reads one redacted error envelope and rejects accidental duplicate command output.
    let private assertSingleRedactedCancellation (output: string) =
        Assert.That(Regex.Matches(output, "\\\"Error\\\"").Count, Is.EqualTo(1))

        use document = JsonDocument.Parse(output)
        let root = document.RootElement
        Assert.That(root.GetProperty("Error").GetString(), Is.EqualTo("The command was canceled."))
        Assert.That(output, Does.Not.Contain("OperationCanceledException"))
        Assert.That(output, Does.Not.Contain("Stack trace"))

    /// Proves the caller token reaches the selected async action without changing its normal exit code.
    [<Test>]
    let ``run passes the caller token to cache status and preserves its exit code`` () =
        let mutable receivedToken = CancellationToken.None
        let initializer () = Assert.Fail("Cache status must not initialize execution dependencies.")

        let handler cancellationToken =
            receivedToken <- cancellationToken
            Task.FromResult(37)

        use cancellation = new CancellationTokenSource()

        let exitCode, output = runWithCapturedOutput (dependencies initializer handler) [| "cache"; "status" |] cancellation.Token

        Assert.That(exitCode, Is.EqualTo(37))
        Assert.That(output, Is.Empty)
        Assert.That(receivedToken.CanBeCanceled, Is.True)

    /// Proves a pre-cancelled caller token produces one stable redacted nonzero root result.
    [<Test>]
    let ``run returns one redacted nonzero result for pre-cancelled cache status`` () =
        let initializer () = Assert.Fail("Cache status must not initialize execution dependencies.")

        let handler (cancellationToken: CancellationToken) : Task<int> =
            cancellationToken.ThrowIfCancellationRequested()
            Task.FromResult(0)

        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        let exitCode, output = runWithCapturedOutput (dependencies initializer handler) [| "cache"; "status" |] cancellation.Token

        Assert.That(exitCode, Is.Not.EqualTo(0))
        assertSingleRedactedCancellation output

    /// Proves cancellation after deterministic action entry returns one stable redacted nonzero root result.
    [<Test>]
    let ``run returns one redacted nonzero result after cache status action entry`` () =
        let entered = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        let initializer () = Assert.Fail("Cache status must not initialize execution dependencies.")

        let handler (cancellationToken: CancellationToken) : Task<int> =
            task {
                entered.TrySetResult() |> ignore
                do! release.Task
                cancellationToken.ThrowIfCancellationRequested()
                return 0
            }

        use cancellation = new CancellationTokenSource()
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer

            let invocation = GraceCommand.run (dependencies initializer handler) [| "cache"; "status" |] cancellation.Token
            entered.Task.GetAwaiter().GetResult()
            cancellation.Cancel()
            release.TrySetResult() |> ignore

            let exitCode = invocation.GetAwaiter().GetResult()
            Assert.That(exitCode, Is.Not.EqualTo(0))
            assertSingleRedactedCancellation (writer.ToString())
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    /// Proves a token cancelled after action completion cannot overwrite the completed domain exit code.
    [<Test>]
    let ``run preserves a completed cache status exit when cancellation races after completion`` () =
        let completed = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        let initializer () = Assert.Fail("Cache status must not initialize execution dependencies.")

        let handler (_: CancellationToken) : Task<int> =
            completed.TrySetResult() |> ignore
            Task.FromResult(37)

        use cancellation = new CancellationTokenSource()
        let invocation = GraceCommand.run (dependencies initializer handler) [| "cache"; "status" |] cancellation.Token
        completed.Task.GetAwaiter().GetResult()
        cancellation.Cancel()

        let exitCode = invocation.GetAwaiter().GetResult()
        Assert.That(exitCode, Is.EqualTo(37))

    /// Proves valid Cache status introspection bypasses all injected execution initialization and handler work.
    [<TestCase("--schema")>]
    [<TestCase("--examples")>]
    let ``cache status introspection is inert before execution initialization`` introspectionOption =
        let mutable initializerCalls = 0
        let mutable handlerCalls = 0

        let initializer () = initializerCalls <- initializerCalls + 1

        let handler _ =
            handlerCalls <- handlerCalls + 1
            Task.FromResult(0)

        let exitCode, output =
            runWithCapturedOutput
                (dependencies initializer handler)
                [|
                    "cache"
                    "status"
                    introspectionOption
                |]
                CancellationToken.None

        Assert.That(exitCode, Is.EqualTo(0))
        Assert.That(initializerCalls, Is.EqualTo(0))
        Assert.That(handlerCalls, Is.EqualTo(0))
        Assert.That(output, Does.Contain("Registry"))

    /// Proves introspection remains strict for mutually exclusive options, unknown options, and malformed values.
    [<TestCase("--schema", "--examples", "cache", "status")>]
    [<TestCase("cache", "status", "--schema", "--unknown")>]
    [<TestCase("--output", "not-an-output-mode", "cache", "status", "--schema")>]
    [<TestCase("--output", "not-an-output-mode", "--select", "Enrollment", "cache", "status", "--schema")>]
    let ``cache status introspection rejects invalid option combinations`` ([<ParamArray>] args: string array) =
        let mutable initializerCalls = 0
        let mutable handlerCalls = 0

        let initializer () = initializerCalls <- initializerCalls + 1

        let handler _ =
            handlerCalls <- handlerCalls + 1
            Task.FromResult(0)

        let exitCode, _ = runWithCapturedOutput (dependencies initializer handler) args CancellationToken.None
        Assert.That(exitCode, Is.Not.EqualTo(0))
        Assert.That(initializerCalls, Is.EqualTo(0))
        Assert.That(handlerCalls, Is.EqualTo(0))
