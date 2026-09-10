namespace Grace.CLI.Tests

open Grace.CLI
open Grace.CLI.Command
open Grace.Shared.Parameters.Library
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Library
open NUnit.Framework
open System
open System.IO
open System.Threading
open System.Threading.Tasks

/// Tests catalog command request construction without changing global SDK clients or invoking a server.
[<NonParallelizable>]
module LibraryCommandTests =

    /// Invokes the real command action with cancellation while another holder owns the configured root lease.
    [<Test>]
    let ``adopt catalog command cancellation while waiting leaves participation untouched`` () =
        task {
            let previousDirectory = Environment.CurrentDirectory
            let root = Path.Combine(Path.GetTempPath(), $"grace-adoption-command-{Guid.NewGuid():N}")
            let grace = Directory.CreateDirectory(Path.Combine(root, ".grace"))
            let configuration = Grace.Shared.Client.Configuration.GraceConfiguration()
            configuration.OwnerId <- Guid.NewGuid()
            configuration.OrganizationId <- Guid.NewGuid()
            configuration.RepositoryId <- Guid.NewGuid()
            Grace.Shared.Client.Configuration.saveConfigFile (Path.Combine(grace.FullName, "graceconfig.json")) configuration

            try
                Environment.CurrentDirectory <- root
                Grace.Shared.Client.Configuration.resetConfiguration ()
                let current = Grace.Shared.Client.Configuration.Current()
                do! LibraryLocalState.initialize current.GraceStatusFile

                let before: LibraryLocalState.RepositoryState =
                    {
                        RepositoryId = current.RepositoryId
                        WorkingCopyId = Guid.NewGuid()
                        Catalog =
                            {
                                RepositoryId = current.RepositoryId
                                Version = Guid.NewGuid()
                                PreviousVersion = None
                                Libraries = [| "Library" |]
                                CreatedAt = getCurrentInstant ()
                                CreatedBy = "test"
                            }
                        CursorEpoch = LibraryCursorEpoch.ofGuid (Guid.NewGuid())
                        AppliedCursor = "opaque-command-predecessor"
                        NextPageToken = None
                        State = "current"
                        Paused = true
                        Baseline = None
                    }

                LibraryLocalState.enable current.GraceStatusFile before

                let scope =
                    WorkingDirectoryUpdateCoordination.Scope.create current.RepositoryId current.RootDirectory
                    |> Result.defaultWith invalidOp

                use! held = WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None
                use cancellation = new CancellationTokenSource()

                let parsed =
                    GraceCommand.rootCommand.Parse [| "library"
                                                      "sync"
                                                      "adopt-catalog"
                                                      "--output"
                                                      "Json" |]

                Assert.That(parsed.Errors.Count, Is.Zero)
                let action = parsed.CommandResult.Command.Action :?> System.CommandLine.Invocation.AsynchronousCommandLineAction
                let waiting = action.InvokeAsync(parsed, cancellation.Token)
                Assert.That(waiting.IsCompleted, Is.False)
                cancellation.Cancel()
                let! exitCode = waiting
                Assert.That(exitCode, Is.Not.Zero)
                Assert.That(LibraryLocalState.readRepository current.GraceStatusFile current.RepositoryId, Is.EqualTo(Some before))
                Assert.That(LibraryLocalState.readOperations current.GraceStatusFile current.RepositoryId, Is.Empty)
            finally
                Environment.CurrentDirectory <- previousDirectory
                Grace.Shared.Client.Configuration.resetConfiguration ()
        }

    /// Keeps human output explicit about rejection, unknown acceptance, incomplete accepted application and finished filenames.
    [<TestCase("completed", "completed");
      TestCase("rejected", "rejected");
      TestCase("ambiguous", "ambiguous");
      TestCase("acceptedButObstructed", "local application is incomplete")>]
    let ``rename human output distinguishes every retained outcome`` outcome expected =
        let typedOutcome =
            match outcome with
            | "completed" -> LibrarySynchronization.RenameOutcome.Completed
            | "rejected" -> LibrarySynchronization.RenameOutcome.Rejected LibraryOperation.RejectionCode.SlotOccupied
            | "ambiguous" -> LibrarySynchronization.RenameOutcome.Ambiguous(Some "detail")
            | _ -> LibrarySynchronization.RenameOutcome.AcceptedButObstructed(Some "detail")

        let result: LibrarySynchronization.RenameResult =
            { OperationId = Guid.NewGuid(); SourcePath = "Library/a.txt"; TargetPath = "Library/b.txt"; Outcome = typedOutcome }

        let message = LibraryCommand.renameMessage result
        Assert.That(message, Does.Contain(expected))
        let output = LibraryCommand.renameOutput result
        Assert.That(output.Outcome, Is.EqualTo(outcome))
        Assert.That(output.ReasonCode, Is.EqualTo(if outcome = "rejected" then Some RejectionReason.SlotOccupied else None))

        Assert.That(
            output.Diagnostic,
            Is.EqualTo(
                if outcome = "ambiguous"
                   || outcome = "acceptedButObstructed" then
                    Some "detail"
                else
                    None
            )
        )

        if outcome <> "completed" then
            Assert.That(message, Does.Not.Contain("completed:"))

    /// Supplies fixed repository scope independent of the current working copy.
    let private ownerId = Guid.Parse "a866eac9-c4aa-496b-aef7-851cc9dbe059"
    let private organizationId = Guid.Parse "d9be512f-c4a0-48d7-ae24-9e383dfeab1f"
    let private repositoryId = Guid.Parse "522c661e-445b-435c-a092-d073d903083f"
    let private correlationId = "library-command-options"

    /// Parses the actual command with explicit scope and the chosen optional arguments.
    let private parse verb options =
        let parsed =
            GraceCommand.rootCommand.Parse(
                Array.concat [ [|
                                   "library"
                                   verb
                                   "shared/docs"
                                   "--owner-id"
                                   string ownerId
                                   "--organization-id"
                                   string organizationId
                                   "--repository-id"
                                   string repositoryId
                                   "--correlation-id"
                                   correlationId
                               |]
                               options ]
            )

        Assert.That(parsed.Errors.Count, Is.Zero)
        parsed

    /// Supplies one fetched catalog version that is deliberately distinct from an explicit version.
    let private catalog () =
        {
            RepositoryId = repositoryId
            Version = Guid.NewGuid()
            Libraries = [| "shared/docs" |]
            CreatedAt = getCurrentInstant ()
            CreatedBy = "test"
            PreviousVersion = None
        }

    /// Requires catalog reads and mutations to use the same explicitly selected repository scope.
    let private assertScope (parameters: LibraryParameters) =
        Assert.That(parameters.OwnerId, Is.EqualTo(string ownerId))
        Assert.That(parameters.OrganizationId, Is.EqualTo(string organizationId))
        Assert.That(parameters.RepositoryId, Is.EqualTo(string repositoryId))
        Assert.That(parameters.CorrelationId, Is.EqualTo(correlationId))

    /// Returns the server's ordinary accepted or stale catalog outcome unchanged to the handler.
    let private response catalog operationId outcome reason : GraceResult<LibraryCatalogChangeResultDto> =
        Ok(
            GraceReturnValue.Create
                { OperationId = operationId; Outcome = outcome; LibraryCatalog = catalog; ReasonCode = reason; RecordedAt = getCurrentInstant () }
                correlationId
        )

    /// Covers each omitted/explicit version and ID combination through both actual parsed command verbs.
    [<TestCase("add", false, false)>]
    [<TestCase("add", false, true)>]
    [<TestCase("add", true, false)>]
    [<TestCase("add", true, true)>]
    [<TestCase("remove", false, false)>]
    [<TestCase("remove", false, true)>]
    [<TestCase("remove", true, false)>]
    [<TestCase("remove", true, true)>]
    let ``catalog mutation uses one selected version and one operation identity`` verb explicitVersion explicitId =
        task {
            let catalog = catalog ()
            let suppliedVersion, suppliedId, generatedId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()

            let options =
                Array.concat [ (if explicitVersion then
                                    [|
                                        "--expected-version"
                                        string suppliedVersion
                                    |]
                                else
                                    [||])
                               (if explicitId then
                                    [|
                                        "--operation-id"
                                        string suppliedId
                                    |]
                                else
                                    [||]) ]

            let parsed = parse verb options
            let mutable lookups, generated, additions, removals = 0, 0, 0, 0
            let expectedVersion = if explicitVersion then suppliedVersion else catalog.Version
            let expectedId = if explicitId then suppliedId else generatedId

            /// Counts invocation-local identity creation separately from server calls.
            let newId () =
                generated <- generated + 1
                generatedId

            /// Verifies the optional read carries the actual parsed scope.
            let getCatalog parameters =
                lookups <- lookups + 1
                assertScope parameters
                Task.FromResult(Ok(GraceReturnValue.Create catalog correlationId))

            /// Checks all mutation preconditions without substituting a different test-only request builder.
            let assertMutation (parameters: LibraryParameters) (path: string) (version: Guid) (id: Guid) =
                assertScope parameters
                Assert.That(path, Is.EqualTo("shared/docs"))
                Assert.That(version, Is.EqualTo(expectedVersion))
                Assert.That(id, Is.EqualTo(expectedId))

            /// Records the single add request selected by the command.
            let add (parameters: AddLibraryParameters) =
                additions <- additions + 1
                assertMutation parameters parameters.LibraryPath parameters.ExpectedVersion parameters.OperationId
                Task.FromResult(response catalog parameters.OperationId OutcomeKind.Accepted None)

            /// Records the single remove request selected by the command.
            let remove (parameters: RemoveLibraryParameters) =
                removals <- removals + 1
                assertMutation parameters parameters.LibraryPath parameters.ExpectedVersion parameters.OperationId
                Task.FromResult(response catalog parameters.OperationId OutcomeKind.Accepted None)

            let! actual = LibraryCommand.changeLibraryHandlerWith newId getCatalog add remove (verb = "add") parsed
            Assert.That(lookups, Is.EqualTo(if explicitVersion then 0 else 1))
            Assert.That(generated, Is.EqualTo(if explicitId then 0 else 1))
            Assert.That(additions, Is.EqualTo(if verb = "add" then 1 else 0))
            Assert.That(removals, Is.EqualTo(if verb = "remove" then 1 else 0))

            match actual with
            | Ok value -> Assert.That(value.ReturnValue.OperationId, Is.EqualTo(expectedId))
            | Error error -> Assert.Fail(error.Error)
        }

    /// A failed lookup must prevent both kinds of mutation and preserve the actual SDK error.
    [<TestCase("add")>]
    [<TestCase("remove")>]
    let ``catalog lookup failure aborts before mutation`` verb =
        task {
            let error = GraceError.Create "lookup denied" correlationId
            let mutable lookups, mutations = 0, 0

            /// Returns an observable failed lookup under the selected repository scope.
            let getCatalog parameters =
                lookups <- lookups + 1
                assertScope parameters
                Task.FromResult(Error error)

            /// Fails the test if a lookup error is ignored by the command.
            let mutate _ =
                mutations <- mutations + 1
                Task.FromResult(response (catalog ()) Guid.Empty OutcomeKind.Accepted None)

            let! actual = LibraryCommand.changeLibraryHandlerWith Guid.NewGuid getCatalog mutate mutate (verb = "add") (parse verb [||])
            Assert.That(lookups, Is.EqualTo(1))
            Assert.That(mutations, Is.Zero)

            match actual with
            | Error actual -> Assert.That(actual, Is.SameAs(error))
            | Ok _ -> Assert.Fail("Lookup failure must remain an error.")
        }

    /// A catalog change between lookup and mutation stays visible as stale with no reread or retry.
    [<TestCase("add")>]
    [<TestCase("remove")>]
    let ``stale catalog response returns unchanged without retrying the request`` verb =
        task {
            let selected = catalog ()
            let newer = { selected with Version = Guid.NewGuid() }
            let id = Guid.NewGuid()
            let stale = response newer id OutcomeKind.Rejected (Some OutcomeKind.StalePolicy)
            let mutable lookups, mutations, generated = 0, 0, 0

            /// Supplies exactly the old catalog version before the server changes it.
            let getCatalog _ =
                lookups <- lookups + 1
                Task.FromResult(Ok(GraceReturnValue.Create selected correlationId))

            /// Generates the sole operation identity used by this invocation.
            let newId () =
                generated <- generated + 1
                id

            /// Verifies the stale request was sent using the fetched version, not the newer response catalog.
            let check (version: Guid) (operationId: Guid) =
                mutations <- mutations + 1
                Assert.That(version, Is.EqualTo(selected.Version))
                Assert.That(operationId, Is.EqualTo(id))
                Task.FromResult stale

            let! actual =
                LibraryCommand.changeLibraryHandlerWith
                    newId
                    getCatalog
                    (fun parameters -> check parameters.ExpectedVersion parameters.OperationId)
                    (fun parameters -> check parameters.ExpectedVersion parameters.OperationId)
                    (verb = "add")
                    (parse verb [||])

            Assert.That(actual, Is.EqualTo(stale))
            Assert.That(lookups, Is.EqualTo(1))
            Assert.That(mutations, Is.EqualTo(1))
            Assert.That(generated, Is.EqualTo(1))
        }

    /// Explicit empty GUIDs retain server validation semantics rather than becoming implicit defaults.
    [<TestCase("add")>]
    [<TestCase("remove")>]
    let ``explicit zero GUID values do not trigger lookup or identity generation`` verb =
        task {
            let mutable lookups, generated, mutations = 0, 0, 0
            let error = GraceError.Create "server rejects explicit zero" correlationId

            /// Detects accidental replacement of an explicitly supplied empty operation identity.
            let newId () =
                generated <- generated + 1
                Guid.NewGuid()

            /// Detects accidental lookup for an explicitly supplied empty expected version.
            let getCatalog _ =
                lookups <- lookups + 1
                Task.FromResult(Ok(GraceReturnValue.Create (catalog ()) correlationId))

            /// Records that the exact explicit invalid values reach the existing server validation boundary.
            let check (version: Guid) (id: Guid) =
                mutations <- mutations + 1
                Assert.That(version, Is.EqualTo(Guid.Empty))
                Assert.That(id, Is.EqualTo(Guid.Empty))
                Task.FromResult(Error error)

            let! actual =
                LibraryCommand.changeLibraryHandlerWith
                    newId
                    getCatalog
                    (fun parameters -> check parameters.ExpectedVersion parameters.OperationId)
                    (fun parameters -> check parameters.ExpectedVersion parameters.OperationId)
                    (verb = "add")
                    (parse
                        verb
                        [|
                            "--expected-version"
                            string Guid.Empty
                            "--operation-id"
                            string Guid.Empty
                        |])

            Assert.That(lookups, Is.Zero)
            Assert.That(generated, Is.Zero)
            Assert.That(mutations, Is.EqualTo(1))

            match actual with
            | Error actual -> Assert.That(actual, Is.SameAs(error))
            | Ok _ -> Assert.Fail("Server rejection must remain visible.")
        }
