namespace Grace.Server.Tests

open FsCheck
open FsCheck.NUnit
open Grace.Server
open Grace.Shared.Validation.Utilities
open Grace.Types.Artifact
open Grace.Shared.Parameters.WorkItem
open Grace.Shared.Validation.Errors
open Grace.Types.Common
open Grace.Types.WorkItem
open NUnit.Framework
open NodaTime
open System
open System.Collections.Generic
open System.Text

/// Covers work Item Server Unit behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type WorkItemServerUnitTests() =
    /// Constructs metadata fixtures used by the server unit work Item assertions.
    let metadata timestamp =
        {
            Timestamp = timestamp
            CorrelationId = "corr-work-item"
            Principal = "tester"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = Dictionary<string, string>()
        }

    /// Runs validation through the server unit work Item path exercised by these tests.
    let runValidation (validation: Threading.Tasks.ValueTask<Result<unit, WorkItemError>>) =
        validation.AsTask()
        |> Async.AwaitTask
        |> Async.RunSynchronously

    /// Extracts validation Error from the scenario result so assertions stay focused on server unit work Item behavior.
    let getValidationError validations =
        validations
        |> getFirstError
        |> Async.AwaitTask
        |> Async.RunSynchronously

    /// Applies case Pattern inputs to drive the server unit work Item state transition under test.
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

    /// Verifies that update Commands Empty When No Fields Provided.
    [<Test>]
    member _.UpdateCommandsEmptyWhenNoFieldsProvided() =
        let parameters = UpdateWorkItemParameters(WorkItemId = Guid.NewGuid().ToString())
        let commands = WorkItem.buildUpdateCommands parameters
        Assert.That(commands, Is.Empty)

    /// Verifies that update Commands Ordered For Multiple Fields.
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

    /// Verifies that link Reference Validation Rejects Invalid Reference Id.
    [<Test>]
    member _.LinkReferenceValidationRejectsInvalidReferenceId() =
        let parameters = LinkReferenceParameters(WorkItemId = Guid.NewGuid().ToString(), ReferenceId = "not-a-guid")

        let validations = WorkItem.validateLinkReferenceParameters parameters

        let error = validations |> getValidationError

        Assert.That(error, Is.EqualTo(Some WorkItemError.InvalidReferenceId))

    /// Verifies that link Reference Validation Accepts Valid Parameters.
    [<Test>]
    member _.LinkReferenceValidationAcceptsValidParameters() =
        let parameters = LinkReferenceParameters(WorkItemId = Guid.NewGuid().ToString(), ReferenceId = Guid.NewGuid().ToString())

        let validations = WorkItem.validateLinkReferenceParameters parameters

        let error = validations |> getValidationError

        Assert.That(error, Is.EqualTo(None: WorkItemError option))

    /// Verifies that work Item Identifier Validation Accepts Positive Number.
    [<Test>]
    member _.WorkItemIdentifierValidationAcceptsPositiveNumber() =
        let result =
            WorkItem.validateWorkItemIdentifier "123"
            |> runValidation

        Assert.That(result, Is.EqualTo(Ok(): Result<unit, WorkItemError>))

    /// Verifies that work Item Identifier Validation Rejects Non Positive Number.
    [<Test>]
    member _.WorkItemIdentifierValidationRejectsNonPositiveNumber() =
        let result =
            WorkItem.validateWorkItemIdentifier "0"
            |> runValidation

        Assert.That(result, Is.EqualTo(Error WorkItemError.InvalidWorkItemNumber: Result<unit, WorkItemError>))

    /// Verifies that work Item Identifier Validation Accepts Guid.
    [<Test>]
    member _.WorkItemIdentifierValidationAcceptsGuid() =
        let workItemId = Guid.NewGuid().ToString()

        let result =
            WorkItem.validateWorkItemIdentifier workItemId
            |> runValidation

        Assert.That(result, Is.EqualTo(Ok(): Result<unit, WorkItemError>))

    /// Verifies that work Item Identifier Validation Accepts Uppercase Guid.
    [<Test>]
    member _.WorkItemIdentifierValidationAcceptsUppercaseGuid() =
        let workItemId = Guid.NewGuid().ToString().ToUpperInvariant()

        let result =
            WorkItem.validateWorkItemIdentifier workItemId
            |> runValidation

        Assert.That(result, Is.EqualTo(Ok(): Result<unit, WorkItemError>))

    /// Verifies that work Item Identifier Validation Accepts Very Large Positive Number.
    [<Test>]
    member _.WorkItemIdentifierValidationAcceptsVeryLargePositiveNumber() =
        let result =
            Int64.MaxValue.ToString()
            |> WorkItem.validateWorkItemIdentifier
            |> runValidation

        Assert.That(result, Is.EqualTo(Ok(): Result<unit, WorkItemError>))

    /// Verifies that work Item Identifier Validation Rejects Invalid Identifier.
    [<Test>]
    member _.WorkItemIdentifierValidationRejectsInvalidIdentifier() =
        let result =
            WorkItem.validateWorkItemIdentifier "not-a-guid-or-number"
            |> runValidation

        Assert.That(result, Is.EqualTo(Error WorkItemError.InvalidWorkItemId: Result<unit, WorkItemError>))

    /// Verifies that link Promotion Set Validation Rejects Invalid Promotion Set Id.
    [<Test>]
    member _.LinkPromotionSetValidationRejectsInvalidPromotionSetId() =
        let parameters = LinkPromotionSetParameters(WorkItemId = Guid.NewGuid().ToString(), PromotionSetId = "not-a-guid")

        let validations = WorkItem.validateLinkPromotionSetParameters parameters

        let error = validations |> getValidationError

        Assert.That(error, Is.EqualTo(Some WorkItemError.InvalidPromotionSetId))

    /// Verifies that link Promotion Set Validation Accepts Valid Parameters.
    [<Test>]
    member _.LinkPromotionSetValidationAcceptsValidParameters() =
        let parameters = LinkPromotionSetParameters(WorkItemId = Guid.NewGuid().ToString(), PromotionSetId = Guid.NewGuid().ToString())

        let validations = WorkItem.validateLinkPromotionSetParameters parameters

        let error = validations |> getValidationError

        Assert.That(error, Is.EqualTo(None: WorkItemError option))

    /// Verifies that link Artifact Validation Rejects Invalid Artifact Id.
    [<Test>]
    member _.LinkArtifactValidationRejectsInvalidArtifactId() =
        let parameters = LinkArtifactParameters(WorkItemId = Guid.NewGuid().ToString(), ArtifactId = "not-a-guid")

        let validations = WorkItem.validateLinkArtifactParameters parameters

        let error = validations |> getValidationError

        Assert.That(error, Is.EqualTo(Some WorkItemError.InvalidArtifactId))

    /// Verifies that link Artifact Validation Accepts Valid Parameters.
    [<Test>]
    member _.LinkArtifactValidationAcceptsValidParameters() =
        let parameters = LinkArtifactParameters(WorkItemId = Guid.NewGuid().ToString(), ArtifactId = Guid.NewGuid().ToString())

        let validations = WorkItem.validateLinkArtifactParameters parameters

        let error = validations |> getValidationError

        Assert.That(error, Is.EqualTo(None: WorkItemError option))

    /// Verifies that add Summary Validation Accepts Guid Identifier.
    [<Test>]
    member _.AddSummaryValidationAcceptsGuidIdentifier() =
        let parameters = AddSummaryParameters(WorkItemId = Guid.NewGuid().ToString(), SummaryContent = "Summary content")

        let result = WorkItem.validateAddSummaryParameters parameters

        match result with
        | Ok _ -> Assert.Pass()
        | Error errorMessage -> Assert.Fail($"Expected validation to succeed, but received '{errorMessage}'.")

    /// Verifies that add Summary Validation Accepts Numeric Identifier.
    [<Test>]
    member _.AddSummaryValidationAcceptsNumericIdentifier() =
        let parameters = AddSummaryParameters(WorkItemId = "42", SummaryContent = "Summary content")

        let result = WorkItem.validateAddSummaryParameters parameters

        match result with
        | Ok _ -> Assert.Pass()
        | Error errorMessage -> Assert.Fail($"Expected validation to succeed, but received '{errorMessage}'.")

    /// Verifies that add Summary Validation Rejects Non Positive Numeric Identifier.
    [<Test>]
    member _.AddSummaryValidationRejectsNonPositiveNumericIdentifier() =
        let parameters = AddSummaryParameters(WorkItemId = "0", SummaryContent = "Summary content")

        match WorkItem.validateAddSummaryParameters parameters with
        | Ok _ -> Assert.Fail("Expected validation to reject non-positive WorkItemNumber.")
        | Error errorMessage -> Assert.That(errorMessage, Does.Contain(WorkItemError.getErrorMessage WorkItemError.InvalidWorkItemNumber))

    /// Verifies that add Summary Validation Rejects Unsupported Artifact Reference Mode.
    [<Test>]
    member _.AddSummaryValidationRejectsUnsupportedArtifactReferenceMode() =
        let parameters = AddSummaryParameters(WorkItemId = "42", SummaryContent = "Summary content", SummaryArtifactId = Guid.NewGuid().ToString())

        match WorkItem.validateAddSummaryParameters parameters with
        | Ok _ -> Assert.Fail("Expected validation to reject caller-supplied artifact IDs.")
        | Error errorMessage ->
            Assert.That(errorMessage, Does.Contain("Caller-supplied artifact IDs are not supported"))
            Assert.That(errorMessage, Does.Contain(WorkItem.canonicalAddSummaryContractMessage))

    /// Verifies that add Summary Validation Rejects Prompt Origin Without Prompt Content.
    [<Test>]
    member _.AddSummaryValidationRejectsPromptOriginWithoutPromptContent() =
        let parameters = AddSummaryParameters(WorkItemId = "42", SummaryContent = "Summary content", PromptOrigin = "agent://codex")

        match WorkItem.validateAddSummaryParameters parameters with
        | Ok _ -> Assert.Fail("Expected validation to reject PromptOrigin when PromptContent is absent.")
        | Error errorMessage ->
            Assert.That(errorMessage, Does.Contain("PromptOrigin can only be provided when PromptContent is provided"))
            Assert.That(errorMessage, Does.Contain(WorkItem.canonicalAddSummaryContractMessage))

    /// Verifies that add Summary Validation Rejects Invalid Promotion Set Id.
    [<Test>]
    member _.AddSummaryValidationRejectsInvalidPromotionSetId() =
        let parameters = AddSummaryParameters(WorkItemId = "42", SummaryContent = "Summary content", PromotionSetId = "not-a-guid")

        match WorkItem.validateAddSummaryParameters parameters with
        | Ok _ -> Assert.Fail("Expected validation to reject invalid PromotionSetId.")
        | Error errorMessage -> Assert.That(errorMessage, Is.EqualTo("PromotionSetId must be a valid non-empty Guid."))

    /// Verifies that add Summary Artifact Seed Normalizes Correlation Id.
    [<Test>]
    member _.AddSummaryArtifactSeedNormalizesCorrelationId() =
        let repositoryId = Guid.Parse("89f08f88-0d98-4562-a5f7-bce8d4e4c2ec")
        let workItemId = Guid.Parse("6d742a8e-5fd6-4d89-81cd-7ea3005570ef")
        let lowerSeed = WorkItem.buildAddSummaryArtifactSeed repositoryId workItemId "corr:add-summary:summary-artifact"
        let mixedSeed = WorkItem.buildAddSummaryArtifactSeed repositoryId workItemId " CoRR:Add-SuMMary:Summary-Artifact "

        Assert.That(lowerSeed, Is.EqualTo(mixedSeed))

    /// Verifies that deterministic Add Summary Artifact Id Is Stable For Replay.
    [<Test>]
    member _.DeterministicAddSummaryArtifactIdIsStableForReplay() =
        let repositoryId = Guid.Parse("89f08f88-0d98-4562-a5f7-bce8d4e4c2ec")
        let workItemId = Guid.Parse("6d742a8e-5fd6-4d89-81cd-7ea3005570ef")
        let correlationId = "corr:add-summary:summary-artifact"

        let firstId = WorkItem.buildDeterministicAddSummaryArtifactId repositoryId workItemId correlationId
        let replayId = WorkItem.buildDeterministicAddSummaryArtifactId repositoryId workItemId correlationId

        Assert.That(firstId, Is.EqualTo(replayId))

    /// Verifies that deterministic Add Summary Artifact Id Differs By Artifact Segment.
    [<Test>]
    member _.DeterministicAddSummaryArtifactIdDiffersByArtifactSegment() =
        let repositoryId = Guid.Parse("89f08f88-0d98-4562-a5f7-bce8d4e4c2ec")
        let workItemId = Guid.Parse("6d742a8e-5fd6-4d89-81cd-7ea3005570ef")

        let summaryArtifactId = WorkItem.buildDeterministicAddSummaryArtifactId repositoryId workItemId "corr:add-summary:summary-artifact"

        let promptArtifactId = WorkItem.buildDeterministicAddSummaryArtifactId repositoryId workItemId "corr:add-summary:prompt-artifact"

        Assert.That(summaryArtifactId, Is.Not.EqualTo(promptArtifactId))

    /// Verifies that add-summary artifact link metadata carries the PromotionSet scope for public projection gating.
    [<Test>]
    member _.AddSummaryArtifactLinkMetadataCarriesPromotionSetScope() =
        let promotionSetId = Guid.NewGuid()
        let originalMetadata = metadata (Instant.FromUtc(2026, 7, 9, 10, 15))
        originalMetadata.Properties[ "Unrelated" ] <- "kept"

        let scopedMetadata = WorkItem.withPromotionSetProjectionScope (Some promotionSetId) originalMetadata

        Assert.That(scopedMetadata.Properties[nameof PromotionSetId], Is.EqualTo($"{promotionSetId}"))
        Assert.That(scopedMetadata.Properties["Unrelated"], Is.EqualTo("kept"))
        Assert.That(originalMetadata.Properties.ContainsKey(nameof PromotionSetId), Is.False)

    /// Verifies that deterministic Add Summary Artifact Id Differs By Repository Work Item And Correlation Segment.
    [<Test>]
    member _.DeterministicAddSummaryArtifactIdDiffersByRepositoryWorkItemAndCorrelationSegment() =
        let repositoryId = Guid.Parse("89f08f88-0d98-4562-a5f7-bce8d4e4c2ec")
        let otherRepositoryId = Guid.Parse("0242c758-2055-4ae1-80c5-7c1638cc0cdf")
        let workItemId = Guid.Parse("6d742a8e-5fd6-4d89-81cd-7ea3005570ef")
        let otherWorkItemId = Guid.Parse("78ac36f4-9046-482c-981c-d173a3c94e94")
        let correlationId = "corr:add-summary:summary-artifact"

        let baselineId = WorkItem.buildDeterministicAddSummaryArtifactId repositoryId workItemId correlationId
        let otherRepositoryArtifactId = WorkItem.buildDeterministicAddSummaryArtifactId otherRepositoryId workItemId correlationId
        let otherWorkItemArtifactId = WorkItem.buildDeterministicAddSummaryArtifactId repositoryId otherWorkItemId correlationId
        let otherCorrelationSegmentId = WorkItem.buildDeterministicAddSummaryArtifactId repositoryId workItemId "corr:add-summary:prompt-artifact"

        Assert.That(otherRepositoryArtifactId, Is.Not.EqualTo(baselineId))
        Assert.That(otherWorkItemArtifactId, Is.Not.EqualTo(baselineId))
        Assert.That(otherCorrelationSegmentId, Is.Not.EqualTo(baselineId))

    /// Verifies that deterministic Add Summary Blob Path Uses Artifact Identity Partition.
    [<Test>]
    member _.DeterministicAddSummaryBlobPathUsesArtifactIdentityPartition() =
        let artifactId = Guid.Parse("7d535f96-e634-4313-b5ff-d9293ee9db57")
        let blobPath = WorkItem.buildDeterministicAddSummaryBlobPath artifactId

        Assert.That(blobPath, Is.EqualTo("grace-artifacts/by-id/7d535f96-e634-4313-b5ff-d9293ee9db57"))

    /// Verifies that deterministic Add Summary Blob Path Does Not Use Date Partitions.
    [<Test>]
    member _.DeterministicAddSummaryBlobPathDoesNotUseDatePartitions() =
        let artifactId = Guid.Parse("7d535f96-e634-4313-b5ff-d9293ee9db57")
        let blobPath = WorkItem.buildDeterministicAddSummaryBlobPath artifactId

        Assert.That(blobPath.Split('/'), Has.Length.EqualTo(3))
        Assert.That(blobPath, Does.Not.Match(@".*/\d{4}/\d{2}/\d{2}/.*"))

    /// Verifies that add Summary Mime Type Defaults And Trims Whitespace.
    [<Test>]
    member _.AddSummaryMimeTypeDefaultsAndTrimsWhitespace() =
        Assert.That(WorkItem.normalizeAddSummaryMimeType null, Is.EqualTo("text/markdown"))
        Assert.That(WorkItem.normalizeAddSummaryMimeType String.Empty, Is.EqualTo("text/markdown"))
        Assert.That(WorkItem.normalizeAddSummaryMimeType "   ", Is.EqualTo("text/markdown"))
        Assert.That(WorkItem.normalizeAddSummaryMimeType "  application/json  ", Is.EqualTo("application/json"))

    /// Verifies that add Summary Content Hash Uses Lowercase Sha256 Hex.
    [<Test>]
    member _.AddSummaryContentHashUsesLowercaseSha256Hex() =
        let contentBytes = Encoding.UTF8.GetBytes("Grace add-summary content")

        let contentHash = WorkItem.computeSha256 contentBytes

        Assert.That(contentHash, Is.EqualTo("fe7b99b4ee981f8232f58cc18ac51e3999d3c52b01002d1070df8f751c92c423"))
        Assert.That(contentHash, Is.EqualTo(contentHash.ToLowerInvariant()))

    /// Verifies that recoverable Artifact Create Errors Allow Replay Conditions.
    [<Test>]
    member _.RecoverableArtifactCreateErrorsAllowReplayConditions() =
        let duplicateCorrelationError = GraceError.Create "Duplicate correlation ID for Artifact command." "corr-add-summary"
        let existingArtifactError = GraceError.Create "Artifact already exists." "corr-add-summary"
        let fatalError = GraceError.Create "Artifact content upload failed." "corr-add-summary"

        Assert.That(WorkItem.isRecoverableArtifactCreateError duplicateCorrelationError, Is.True)
        Assert.That(WorkItem.isRecoverableArtifactCreateError existingArtifactError, Is.True)
        Assert.That(WorkItem.isRecoverableArtifactCreateError fatalError, Is.False)

    /// Verifies that remove Artifact Type Validation Rejects Missing Artifact Type.
    [<Test>]
    member _.RemoveArtifactTypeValidationRejectsMissingArtifactType() =
        let parameters = RemoveArtifactTypeLinksParameters(WorkItemId = Guid.NewGuid().ToString(), ArtifactType = String.Empty)

        let validations = WorkItem.validateRemoveArtifactTypeLinksParameters parameters

        let error = validations |> getValidationError

        Assert.That(error, Is.EqualTo(Some WorkItemError.InvalidArtifactType))

    /// Verifies that show Attachment Validation Rejects Missing Attachment Type.
    [<Test>]
    member _.ShowAttachmentValidationRejectsMissingAttachmentType() =
        let parameters = ShowWorkItemAttachmentParameters(WorkItemId = Guid.NewGuid().ToString(), AttachmentType = String.Empty)

        let validations = WorkItem.validateShowWorkItemAttachmentParameters parameters

        let error = validations |> getValidationError

        Assert.That(error, Is.EqualTo(Some WorkItemError.InvalidArtifactType))

    /// Verifies that list Attachment Validation Accepts Valid Parameters.
    [<Test>]
    member _.ListAttachmentValidationAcceptsValidParameters() =
        let parameters = ListWorkItemAttachmentsParameters(WorkItemId = Guid.NewGuid().ToString())

        let validations = WorkItem.validateListWorkItemAttachmentsParameters parameters

        let error = validations |> getValidationError

        Assert.That(error, Is.EqualTo(None: WorkItemError option))

    /// Verifies that show Attachment Validation Accepts Valid Parameters.
    [<Test>]
    member _.ShowAttachmentValidationAcceptsValidParameters() =
        let parameters = ShowWorkItemAttachmentParameters(WorkItemId = Guid.NewGuid().ToString(), AttachmentType = "summary")

        let validations = WorkItem.validateShowWorkItemAttachmentParameters parameters

        let error = validations |> getValidationError

        Assert.That(error, Is.EqualTo(None: WorkItemError option))

    /// Verifies that download Attachment Validation Accepts Valid Parameters.
    [<Test>]
    member _.DownloadAttachmentValidationAcceptsValidParameters() =
        let parameters = DownloadWorkItemAttachmentParameters(WorkItemId = Guid.NewGuid().ToString(), ArtifactId = Guid.NewGuid().ToString())

        let validations = WorkItem.validateDownloadWorkItemAttachmentParameters parameters

        let error = validations |> getValidationError

        Assert.That(error, Is.EqualTo(None: WorkItemError option))

    /// Verifies that download Attachment Validation Rejects Missing Artifact Id.
    [<Test>]
    member _.DownloadAttachmentValidationRejectsMissingArtifactId() =
        let parameters = DownloadWorkItemAttachmentParameters(WorkItemId = Guid.NewGuid().ToString(), ArtifactId = String.Empty)

        let validations = WorkItem.validateDownloadWorkItemAttachmentParameters parameters

        let error = validations |> getValidationError

        Assert.That(error, Is.EqualTo(Some WorkItemError.InvalidArtifactId))

    /// Verifies that download Attachment Validation Rejects Empty Guid Artifact Id.
    [<Test>]
    member _.DownloadAttachmentValidationRejectsEmptyGuidArtifactId() =
        let parameters = DownloadWorkItemAttachmentParameters(WorkItemId = Guid.NewGuid().ToString(), ArtifactId = Guid.Empty.ToString())

        let validations = WorkItem.validateDownloadWorkItemAttachmentParameters parameters

        let error = validations |> getValidationError

        Assert.That(error, Is.EqualTo(Some WorkItemError.InvalidArtifactId))

    /// Verifies that download Attachment Validation Rejects Invalid Artifact Id.
    [<Test>]
    member _.DownloadAttachmentValidationRejectsInvalidArtifactId() =
        let parameters = DownloadWorkItemAttachmentParameters(WorkItemId = Guid.NewGuid().ToString(), ArtifactId = "not-a-guid")

        let validations = WorkItem.validateDownloadWorkItemAttachmentParameters parameters

        let error = validations |> getValidationError

        Assert.That(error, Is.EqualTo(Some WorkItemError.InvalidArtifactId))

    /// Verifies that remove Artifact Type Validation Accepts Known Aliases.
    [<Test>]
    member _.RemoveArtifactTypeValidationAcceptsKnownAliases() =
        let aliases =
            [
                "summary"
                "agentsummary"
                "prompt"
                "notes"
                "reviewnotes"
            ]

        for alias in aliases do
            let parameters = RemoveArtifactTypeLinksParameters(WorkItemId = Guid.NewGuid().ToString(), ArtifactType = alias)

            let validations = WorkItem.validateRemoveArtifactTypeLinksParameters parameters

            let error = validations |> getValidationError

            Assert.That(error, Is.EqualTo(None: WorkItemError option), $"Expected artifact type alias '{alias}' to pass validation.")

    /// Verifies that parse Removable Artifact Type Handles Aliases.
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

    /// Verifies that work Item Identifier Validation Accepts Positive Number Strings.
    [<FsCheck.NUnit.Property(MaxTest = 100)>]
    member _.WorkItemIdentifierValidationAcceptsPositiveNumberStrings(positiveInt: PositiveInt) =
        let result =
            int64 positiveInt.Get
            |> string
            |> WorkItem.validateWorkItemIdentifier
            |> runValidation

        result = (Ok(): Result<unit, WorkItemError>)

    /// Verifies that work Item Identifier Validation Rejects Non Positive Number Strings.
    [<FsCheck.NUnit.Property(MaxTest = 100)>]
    member _.WorkItemIdentifierValidationRejectsNonPositiveNumberStrings(value: int) =
        let nonPositiveValue = if value > 0 then -value else value

        let result =
            nonPositiveValue
            |> string
            |> WorkItem.validateWorkItemIdentifier
            |> runValidation

        result = (Error WorkItemError.InvalidWorkItemNumber: Result<unit, WorkItemError>)

    /// Verifies that work Item Identifier Validation Accepts Non Empty Guid Strings.
    [<FsCheck.NUnit.Property(MaxTest = 100)>]
    member _.WorkItemIdentifierValidationAcceptsNonEmptyGuidStrings(guid: Guid) =
        let validGuid = if guid = Guid.Empty then Guid.NewGuid() else guid

        let result =
            validGuid.ToString()
            |> WorkItem.validateWorkItemIdentifier
            |> runValidation

        result = (Ok(): Result<unit, WorkItemError>)

    /// Verifies that parse Removable Artifact Type Is Case Insensitive.
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

    /// Verifies that duplicate Correlation Detection Finds Matches.
    [<Test>]
    member _.DuplicateCorrelationDetectionFindsMatches() =
        let timestamp = Instant.FromUtc(2025, 1, 1, 0, 0)
        let eventMetadata = metadata timestamp
        let workItemEvent = { Event = WorkItemEventType.TitleSet "Title"; Metadata = eventMetadata }

        let duplicate = Grace.Actors.WorkItem.hasDuplicateCorrelationId [ workItemEvent ] eventMetadata
        let different = Grace.Actors.WorkItem.hasDuplicateCorrelationId [ workItemEvent ] { eventMetadata with CorrelationId = "corr-other" }

        Assert.That(duplicate, Is.True)
        Assert.That(different, Is.False)
