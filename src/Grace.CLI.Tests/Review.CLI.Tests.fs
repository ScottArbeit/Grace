namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.SDK
open Grace.Shared
open Grace.Types.Policy
open Grace.Types.PromotionSet
open Grace.Types.Types
open NUnit.Framework
open System
open System.IO
open System.Threading.Tasks

[<NonParallelizable>]
module ReviewCommandTests =
    let private ownerId = Guid.NewGuid()
    let private organizationId = Guid.NewGuid()
    let private repositoryId = Guid.NewGuid()

    let private graceIds =
        { GraceIds.Default with
            OwnerId = ownerId
            OwnerIdString = ownerId.ToString()
            OrganizationId = organizationId
            OrganizationIdString = organizationId.ToString()
            RepositoryId = repositoryId
            RepositoryIdString = repositoryId.ToString()
            CorrelationId = "corr-review"
        }

    let private withIds (args: string array) =
        Array.append
            args
            [|
                "--owner-id"
                ownerId.ToString()
                "--organization-id"
                organizationId.ToString()
                "--repository-id"
                repositoryId.ToString()
            |]

    let private withIdsAndSilent (args: string array) =
        args
        |> Array.append [| "--output"; "Silent" |]
        |> withIds

    let private invokeWithCapturedConsole (parseResult: System.CommandLine.ParseResult) =
        use standardOutWriter = new StringWriter()
        use standardErrorWriter = new StringWriter()
        let originalOut = Console.Out
        let originalError = Console.Error

        try
            Console.SetOut(standardOutWriter)
            Console.SetError(standardErrorWriter)
            let exitCode = parseResult.Invoke()
            exitCode, standardOutWriter.ToString(), standardErrorWriter.ToString()
        finally
            Console.SetOut(originalOut)
            Console.SetError(originalError)

    let private parseCheckpoint (promotionSetId: Guid) (extraArgs: string array) =
        let baseArgs =
            [|
                "review"
                "checkpoint"
                "--promotion-set"
                promotionSetId.ToString()
                "--reference-id"
                Guid.NewGuid().ToString()
            |]

        GraceCommand.rootCommand.Parse(withIdsAndSilent (Array.append baseArgs extraArgs))

    [<Test>]
    let ``review open rejects invalid promotion set id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "review"
                                    "open"
                                    "--promotion-set"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``review checkpoint rejects invalid reference id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "review"
                                    "checkpoint"
                                    "--promotion-set"
                                    Guid.NewGuid().ToString()
                                    "--reference-id"
                                    "not-a-guid"
                                    "--policy-snapshot-id"
                                    "snapshot" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``review resolve requires resolution state`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "review"
                                    "resolve"
                                    "--promotion-set"
                                    Guid.NewGuid().ToString()
                                    "--finding-id"
                                    Guid.NewGuid().ToString() |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``review resolve rejects approve and request changes together`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "review"
                                    "resolve"
                                    "--promotion-set"
                                    Guid.NewGuid().ToString()
                                    "--finding-id"
                                    Guid.NewGuid().ToString()
                                    "--approve"
                                    "--request-changes" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``review delta command is unavailable`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "review"; "delta" |])

        Assert.That(parseResult.Errors.Count, Is.GreaterThan(0))

        let hasDeltaError =
            parseResult.Errors
            |> Seq.exists (fun error -> error.Message.Contains("Unrecognized command or argument 'delta'", StringComparison.OrdinalIgnoreCase))

        Assert.That(hasDeltaError, Is.True)

    [<Test>]
    let ``review checkpoint uses explicit policy snapshot when provided`` () =
        let promotionSetId = Guid.NewGuid()

        let parseResult =
            parseCheckpoint
                promotionSetId
                [|
                    "--policy-snapshot-id"
                    "snapshot-explicit"
                |]

        let promotionSet = { PromotionSetDto.Default with PromotionSetId = promotionSetId; TargetBranchId = Guid.NewGuid() }

        let mutable getPromotionSetCalled = false
        let mutable policyCalled = false

        let getPromotionSet (_: Parameters.PromotionSet.GetPromotionSetParameters) =
            getPromotionSetCalled <- true
            Task.FromResult(Ok(GraceReturnValue.Create promotionSet graceIds.CorrelationId))

        let getPolicy (_: Parameters.Policy.GetPolicyParameters) =
            policyCalled <- true

            Task.FromResult(
                Ok(GraceReturnValue.Create (Some { PolicySnapshot.Default with PolicySnapshotId = PolicySnapshotId "snapshot-policy" }) graceIds.CorrelationId)
            )

        let result =
            ReviewCommand.resolvePolicySnapshotIdWith getPromotionSet getPolicy parseResult graceIds promotionSetId
            |> Async.AwaitTask
            |> Async.RunSynchronously

        match result with
        | Ok snapshotId -> snapshotId |> should equal "snapshot-explicit"
        | Error error -> Assert.Fail error.Error

        Assert.That(getPromotionSetCalled, Is.False)
        Assert.That(policyCalled, Is.False)

    [<Test>]
    let ``review checkpoint falls back to policy snapshot when promotion set snapshot is missing`` () =
        let promotionSetId = Guid.NewGuid()
        let targetBranchId = Guid.NewGuid()
        let parseResult = parseCheckpoint promotionSetId [||]

        let promotionSet = { PromotionSetDto.Default with PromotionSetId = promotionSetId; TargetBranchId = targetBranchId }

        let getPromotionSet (_: Parameters.PromotionSet.GetPromotionSetParameters) =
            Task.FromResult(Ok(GraceReturnValue.Create promotionSet graceIds.CorrelationId))

        let policySnapshot = { PolicySnapshot.Default with PolicySnapshotId = PolicySnapshotId "snapshot-policy"; TargetBranchId = targetBranchId }

        let getPolicy (_: Parameters.Policy.GetPolicyParameters) = Task.FromResult(Ok(GraceReturnValue.Create (Some policySnapshot) graceIds.CorrelationId))

        let result =
            ReviewCommand.resolvePolicySnapshotIdWith getPromotionSet getPolicy parseResult graceIds promotionSetId
            |> Async.AwaitTask
            |> Async.RunSynchronously

        match result with
        | Ok snapshotId -> snapshotId |> should equal "snapshot-policy"
        | Error error -> Assert.Fail error.Error

    [<Test>]
    let ``candidate get rejects invalid candidate id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "candidate"
                                    "get"
                                    "--candidate"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``candidate required actions rejects invalid candidate id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "candidate"
                                    "required-actions"
                                    "--candidate"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``candidate gate rerun requires gate option`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "candidate"
                                    "gate"
                                    "rerun"
                                    "--candidate"
                                    Guid.NewGuid().ToString() |]
            )

        Assert.That(parseResult.Errors.Count, Is.GreaterThan(0))
        let exitCode, _, standardError = invokeWithCapturedConsole parseResult
        exitCode |> should equal 1

        standardError
        |> should contain "Option '--gate' is required."

    [<Test>]
    let ``candidate parser normalizes guid candidate id`` () =
        let candidateId = Guid.NewGuid()

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "candidate"
                                    "get"
                                    "--candidate"
                                    $"  {candidateId.ToString().ToUpperInvariant()}  " |]
            )

        let parsed = CandidateCommand.tryParseCandidateId (parseResult.GetValue("--candidate")) parseResult

        match parsed with
        | Ok canonical -> canonical |> should equal (candidateId.ToString())
        | Error error -> Assert.Fail error.Error

    let private createReportSection (section: string) (title: string) (sourceState: string) (entries: (string * string list) list) =
        let reportSection = ReviewReportSection()
        reportSection.Section <- section
        reportSection.Title <- title
        reportSection.SourceState <- sourceState

        reportSection.Entries <-
            entries
            |> List.map (fun (key, values) ->
                let entry = ReviewReportEntry()
                entry.Key <- key
                entry.Values <- values
                entry)

        reportSection

    let private createSampleReviewReport () =
        let report = ReviewReportResult()
        report.ReviewReportSchemaVersion <- ReviewReportSchema.Version
        report.SectionOrder <- ReviewReportSections.Ordered

        report.Sections <-
            [
                createReportSection
                    ReviewReportSections.BlockingReasonsAndNextActions
                    "Blocking reasons and next actions"
                    "Inferred"
                    [
                        "Blockers",
                        [
                            "High|review-notes-and-checkpoint|Review findings require resolution before candidate promotion can continue."
                        ]
                        "NextActions",
                        [
                            "grace review open --promotion-set 00000000-0000-0000-0000-000000000001"
                        ]
                    ]
                createReportSection
                    ReviewReportSections.CandidateAndPromotionSet
                    "Candidate and PromotionSet identity or status"
                    "Authoritative"
                    [
                        "CandidateId",
                        [
                            "00000000-0000-0000-0000-000000000001"
                        ]
                        "PromotionSetId",
                        [
                            "00000000-0000-0000-0000-000000000001"
                        ]
                    ]
                createReportSection
                    ReviewReportSections.QueueAndRequiredActions
                    "Queue state and required actions"
                    "Authoritative"
                    [
                        "QueueState", [ "Ready" ]
                        "RequiredActions",
                        [
                            "ResolveFindings"
                            "RetryComputation"
                        ]
                    ]
            ]

        report

    [<Test>]
    let ``review report show rejects invalid candidate id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "review"
                                    "report"
                                    "show"
                                    "--candidate"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``review report export requires format and output file`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "review"
                                    "report"
                                    "export"
                                    "--candidate"
                                    Guid.NewGuid().ToString() |]
            )

        Assert.That(parseResult.Errors.Count, Is.GreaterThan(0))
        let exitCode, _, standardError = invokeWithCapturedConsole parseResult
        exitCode |> should equal 1

        standardError
        |> should contain "Option '--format' is required."

        standardError
        |> should contain "Option '--output-file' is required."

    [<Test>]
    let ``review report json serialization includes schema version`` () =
        let report = createSampleReviewReport ()
        let json = ReviewCommand.serializeReviewReportJson report
        Assert.That(json, Does.Contain("\"ReviewReportSchemaVersion\": \"1.0\""))

    [<Test>]
    let ``review report normalization enforces section order`` () =
        let report = createSampleReviewReport ()
        let normalized = ReviewCommand.normalizeReviewReportForOutput report

        normalized.Sections
        |> List.map (fun section -> section.Section)
        |> should
            equal
            [
                ReviewReportSections.CandidateAndPromotionSet
                ReviewReportSections.QueueAndRequiredActions
                ReviewReportSections.BlockingReasonsAndNextActions
            ]

    [<Test>]
    let ``review report markdown rendering is deterministic`` () =
        let report = createSampleReviewReport ()
        let first = ReviewCommand.renderReviewReportMarkdown report
        let second = ReviewCommand.renderReviewReportMarkdown report
        first |> should equal second
