namespace Grace.Server.Tests

open FsCheck
open FsCheck.NUnit
open Grace.Server
open Grace.Shared.Validation.Utilities
open Grace.Types.Artifact
open Grace.Shared.Parameters.WorkItem
open Grace.Shared.Validation.Errors
open Grace.Types.Types
open Grace.Types.WorkItem
open NUnit.Framework
open NodaTime
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type WorkItemServerUnitTests() =
    let metadata timestamp = { Timestamp = timestamp; CorrelationId = "corr-work-item"; Principal = "tester"; Properties = Dictionary<string, string>() }

    let runValidation (validation: Threading.Tasks.ValueTask<Result<unit, WorkItemError>>) =
        validation.AsTask()
        |> Async.AwaitTask
        |> Async.RunSynchronously

    let applyCasePattern (pattern: bool array) (value: string) =
        let toggles = if isNull pattern then [||] else pattern

        value
        |> Seq.mapi (fun index character ->
            if Char.IsLetter character then
                let useUpper = if toggles.Length = 0 then false else toggles[index % toggles.Length]

                if useUpper then
                    Char.ToUpperInvariant character
                else
                    Char.ToLowerInvariant character
            else
                character)
        |> Seq.toArray
        |> String

    [<Test>]
    member _.UpdateCommandsEmptyWhenNoFieldsProvided() =
        let parameters = UpdateWorkItemParameters(WorkItemId = Guid.NewGuid().ToString())
        let commands = WorkItem.buildUpdateCommands parameters
        Assert.That(commands, Is.Empty)

    [<Test>]
    member _.UpdateCommandsOrderedForMultipleFields() =
        let parameters =
            UpdateWorkItemParameters(
                WorkItemId = Guid.NewGuid().ToString(),
                Title = "Title",
                Description = "Description",
                Status = WorkItemStatus.Active.ToString(),
                Constraints = "Constraints",
                Notes = "Notes",
                ArchitecturalNotes = "Architecture",
                MigrationNotes = "Migration"
            )

        let commands = WorkItem.buildUpdateCommands parameters

        let expected: WorkItemCommand list =
            [
                WorkItemCommand.SetTitle "Title"
                WorkItemCommand.SetDescription "Description"
                WorkItemCommand.SetStatus WorkItemStatus.Active
                WorkItemCommand.SetConstraints "Constraints"
                WorkItemCommand.SetNotes "Notes"
                WorkItemCommand.SetArchitecturalNotes "Architecture"
                WorkItemCommand.SetMigrationNotes "Migration"
            ]

        let matches = commands = expected
        Assert.That(matches, Is.True)

    [<Test>]
    member _.LinkReferenceValidationRejectsInvalidReferenceId() =
        let parameters = LinkReferenceParameters(WorkItemId = Guid.NewGuid().ToString(), ReferenceId = "not-a-guid")

        let validations = WorkItem.validateLinkReferenceParameters parameters

        let error =
            validations
            |> getFirstError
            |> Async.AwaitTask
            |> Async.RunSynchronously

        Assert.That(error, Is.EqualTo(Some WorkItemError.InvalidReferenceId))

    [<Test>]
    member _.WorkItemIdentifierValidationAcceptsPositiveNumber() =
        let result =
            WorkItem.validateWorkItemIdentifier "123"
            |> runValidation

        Assert.That(result, Is.EqualTo(Ok(): Result<unit, WorkItemError>))

    [<Test>]
    member _.WorkItemIdentifierValidationRejectsNonPositiveNumber() =
        let result =
            WorkItem.validateWorkItemIdentifier "0"
            |> runValidation

        Assert.That(result, Is.EqualTo(Error WorkItemError.InvalidWorkItemNumber: Result<unit, WorkItemError>))

    [<Test>]
    member _.WorkItemIdentifierValidationAcceptsGuid() =
        let workItemId = Guid.NewGuid().ToString()

        let result =
            WorkItem.validateWorkItemIdentifier workItemId
            |> runValidation

        Assert.That(result, Is.EqualTo(Ok(): Result<unit, WorkItemError>))

    [<Test>]
    member _.WorkItemIdentifierValidationRejectsInvalidIdentifier() =
        let result =
            WorkItem.validateWorkItemIdentifier "not-a-guid-or-number"
            |> runValidation

        Assert.That(result, Is.EqualTo(Error WorkItemError.InvalidWorkItemId: Result<unit, WorkItemError>))

    [<Test>]
    member _.LinkPromotionSetValidationRejectsInvalidPromotionSetId() =
        let parameters = LinkPromotionSetParameters(WorkItemId = Guid.NewGuid().ToString(), PromotionSetId = "not-a-guid")

        let validations = WorkItem.validateLinkPromotionSetParameters parameters

        let error =
            validations
            |> getFirstError
            |> Async.AwaitTask
            |> Async.RunSynchronously

        Assert.That(error, Is.EqualTo(Some WorkItemError.InvalidPromotionSetId))

    [<Test>]
    member _.LinkArtifactValidationRejectsInvalidArtifactId() =
        let parameters = LinkArtifactParameters(WorkItemId = Guid.NewGuid().ToString(), ArtifactId = "not-a-guid")

        let validations = WorkItem.validateLinkArtifactParameters parameters

        let error =
            validations
            |> getFirstError
            |> Async.AwaitTask
            |> Async.RunSynchronously

        Assert.That(error, Is.EqualTo(Some WorkItemError.InvalidArtifactId))

    [<Test>]
    member _.AddSummaryValidationAcceptsGuidIdentifier() =
        let parameters = AddSummaryParameters(WorkItemId = Guid.NewGuid().ToString(), SummaryContent = "Summary content")

        let result = WorkItem.validateAddSummaryParameters parameters

        match result with
        | Ok _ -> Assert.Pass()
        | Error errorMessage -> Assert.Fail($"Expected validation to succeed, but received '{errorMessage}'.")

    [<Test>]
    member _.AddSummaryValidationAcceptsNumericIdentifier() =
        let parameters = AddSummaryParameters(WorkItemId = "42", SummaryContent = "Summary content")

        let result = WorkItem.validateAddSummaryParameters parameters

        match result with
        | Ok _ -> Assert.Pass()
        | Error errorMessage -> Assert.Fail($"Expected validation to succeed, but received '{errorMessage}'.")

    [<Test>]
    member _.AddSummaryValidationRejectsNonPositiveNumericIdentifier() =
        let parameters = AddSummaryParameters(WorkItemId = "0", SummaryContent = "Summary content")

        match WorkItem.validateAddSummaryParameters parameters with
        | Ok _ -> Assert.Fail("Expected validation to reject non-positive WorkItemNumber.")
        | Error errorMessage -> Assert.That(errorMessage, Does.Contain(WorkItemError.getErrorMessage WorkItemError.InvalidWorkItemNumber))

    [<Test>]
    member _.AddSummaryValidationRejectsUnsupportedArtifactReferenceMode() =
        let parameters = AddSummaryParameters(WorkItemId = "42", SummaryContent = "Summary content", SummaryArtifactId = Guid.NewGuid().ToString())

        match WorkItem.validateAddSummaryParameters parameters with
        | Ok _ -> Assert.Fail("Expected validation to reject caller-supplied artifact IDs.")
        | Error errorMessage ->
            Assert.That(errorMessage, Does.Contain("Caller-supplied artifact IDs are not supported"))
            Assert.That(errorMessage, Does.Contain(WorkItem.canonicalAddSummaryContractMessage))

    [<Test>]
    member _.AddSummaryValidationRejectsPromptOriginWithoutPromptContent() =
        let parameters = AddSummaryParameters(WorkItemId = "42", SummaryContent = "Summary content", PromptOrigin = "agent://codex")

        match WorkItem.validateAddSummaryParameters parameters with
        | Ok _ -> Assert.Fail("Expected validation to reject PromptOrigin when PromptContent is absent.")
        | Error errorMessage ->
            Assert.That(errorMessage, Does.Contain("PromptOrigin can only be provided when PromptContent is provided"))
            Assert.That(errorMessage, Does.Contain(WorkItem.canonicalAddSummaryContractMessage))

    [<Test>]
    member _.AddSummaryValidationRejectsInvalidPromotionSetId() =
        let parameters = AddSummaryParameters(WorkItemId = "42", SummaryContent = "Summary content", PromotionSetId = "not-a-guid")

        match WorkItem.validateAddSummaryParameters parameters with
        | Ok _ -> Assert.Fail("Expected validation to reject invalid PromotionSetId.")
        | Error errorMessage -> Assert.That(errorMessage, Is.EqualTo("PromotionSetId must be a valid non-empty Guid."))

    [<Test>]
    member _.AddSummaryArtifactSeedNormalizesCorrelationId() =
        let repositoryId = Guid.Parse("89f08f88-0d98-4562-a5f7-bce8d4e4c2ec")
        let workItemId = Guid.Parse("6d742a8e-5fd6-4d89-81cd-7ea3005570ef")
        let lowerSeed = WorkItem.buildAddSummaryArtifactSeed repositoryId workItemId "corr:add-summary:summary-artifact"
        let mixedSeed = WorkItem.buildAddSummaryArtifactSeed repositoryId workItemId " CoRR:Add-SuMMary:Summary-Artifact "

        Assert.That(lowerSeed, Is.EqualTo(mixedSeed))

    [<Test>]
    member _.DeterministicAddSummaryArtifactIdIsStableForReplay() =
        let repositoryId = Guid.Parse("89f08f88-0d98-4562-a5f7-bce8d4e4c2ec")
        let workItemId = Guid.Parse("6d742a8e-5fd6-4d89-81cd-7ea3005570ef")
        let correlationId = "corr:add-summary:summary-artifact"

        let firstId = WorkItem.buildDeterministicAddSummaryArtifactId repositoryId workItemId correlationId
        let replayId = WorkItem.buildDeterministicAddSummaryArtifactId repositoryId workItemId correlationId

        Assert.That(firstId, Is.EqualTo(replayId))

    [<Test>]
    member _.DeterministicAddSummaryArtifactIdDiffersByArtifactSegment() =
        let repositoryId = Guid.Parse("89f08f88-0d98-4562-a5f7-bce8d4e4c2ec")
        let workItemId = Guid.Parse("6d742a8e-5fd6-4d89-81cd-7ea3005570ef")

        let summaryArtifactId = WorkItem.buildDeterministicAddSummaryArtifactId repositoryId workItemId "corr:add-summary:summary-artifact"

        let promptArtifactId = WorkItem.buildDeterministicAddSummaryArtifactId repositoryId workItemId "corr:add-summary:prompt-artifact"

        Assert.That(summaryArtifactId, Is.Not.EqualTo(promptArtifactId))

    [<Test>]
    member _.DeterministicAddSummaryBlobPathUsesArtifactIdentityPartition() =
        let artifactId = Guid.Parse("7d535f96-e634-4313-b5ff-d9293ee9db57")
        let blobPath = WorkItem.buildDeterministicAddSummaryBlobPath artifactId

        Assert.That(blobPath, Is.EqualTo("grace-artifacts/by-id/7d535f96-e634-4313-b5ff-d9293ee9db57"))

    [<Test>]
    member _.RemoveArtifactTypeValidationRejectsMissingArtifactType() =
        let parameters = RemoveArtifactTypeLinksParameters(WorkItemId = Guid.NewGuid().ToString(), ArtifactType = String.Empty)

        let validations = WorkItem.validateRemoveArtifactTypeLinksParameters parameters

        let error =
            validations
            |> getFirstError
            |> Async.AwaitTask
            |> Async.RunSynchronously

        Assert.That(error, Is.EqualTo(Some WorkItemError.InvalidArtifactType))

    [<Test>]
    member _.ParseRemovableArtifactTypeHandlesAliases() =
        let expectedMappings =
            [
                "summary", ArtifactType.AgentSummary
                "agentsummary", ArtifactType.AgentSummary
                "prompt", ArtifactType.Prompt
                "notes", ArtifactType.ReviewNotes
                "reviewnotes", ArtifactType.ReviewNotes
            ]

        for (alias, expectedType) in expectedMappings do
            match WorkItem.parseRemovableArtifactType alias with
            | Ok artifactType -> Assert.That(artifactType, Is.EqualTo(expectedType))
            | Error error -> Assert.Fail($"Expected alias '{alias}' to parse, but received {error}.")

    [<FsCheck.NUnit.Property(MaxTest = 100)>]
    member _.WorkItemIdentifierValidationAcceptsPositiveNumberStrings(positiveInt: PositiveInt) =
        let result =
            int64 positiveInt.Get
            |> string
            |> WorkItem.validateWorkItemIdentifier
            |> runValidation

        result = (Ok(): Result<unit, WorkItemError>)

    [<FsCheck.NUnit.Property(MaxTest = 100)>]
    member _.WorkItemIdentifierValidationRejectsNonPositiveNumberStrings(value: int) =
        let nonPositiveValue = if value > 0 then -value else value

        let result =
            nonPositiveValue
            |> string
            |> WorkItem.validateWorkItemIdentifier
            |> runValidation

        result = (Error WorkItemError.InvalidWorkItemNumber: Result<unit, WorkItemError>)

    [<FsCheck.NUnit.Property(MaxTest = 100)>]
    member _.WorkItemIdentifierValidationAcceptsNonEmptyGuidStrings(guid: Guid) =
        let validGuid = if guid = Guid.Empty then Guid.NewGuid() else guid

        let result =
            validGuid.ToString()
            |> WorkItem.validateWorkItemIdentifier
            |> runValidation

        result = (Ok(): Result<unit, WorkItemError>)

    [<FsCheck.NUnit.Property(MaxTest = 100)>]
    member _.ParseRemovableArtifactTypeIsCaseInsensitive(pattern: bool array) =
        let expectedMappings =
            [
                "summary", ArtifactType.AgentSummary
                "agentsummary", ArtifactType.AgentSummary
                "prompt", ArtifactType.Prompt
                "notes", ArtifactType.ReviewNotes
                "reviewnotes", ArtifactType.ReviewNotes
            ]

        expectedMappings
        |> List.forall (fun (alias, expectedType) ->
            let caseVariant = applyCasePattern pattern alias

            match WorkItem.parseRemovableArtifactType caseVariant with
            | Ok artifactType -> artifactType = expectedType
            | Error _ -> false)

    [<Test>]
    member _.DuplicateCorrelationDetectionFindsMatches() =
        let timestamp = Instant.FromUtc(2025, 1, 1, 0, 0)
        let eventMetadata = metadata timestamp
        let workItemEvent = { Event = WorkItemEventType.TitleSet "Title"; Metadata = eventMetadata }

        let duplicate = Grace.Actors.WorkItem.hasDuplicateCorrelationId [ workItemEvent ] eventMetadata
        let different = Grace.Actors.WorkItem.hasDuplicateCorrelationId [ workItemEvent ] { eventMetadata with CorrelationId = "corr-other" }

        Assert.That(duplicate, Is.True)
        Assert.That(different, Is.False)
