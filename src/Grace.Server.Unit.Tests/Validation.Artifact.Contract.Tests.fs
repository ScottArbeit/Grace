namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared.Parameters.Validation
open Grace.Shared.Validation
open Grace.Shared.Validation.Errors
open Grace.Types.Artifact
open NodaTime
open NUnit.Framework
open System

/// Covers validation Artifact Contract behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type ValidationArtifactContractTests() =

    /// Verifies that artifact Blob Path Matches Contract.
    [<Test>]
    member _.ArtifactBlobPathMatchesContract() =
        let artifactId = Guid.Parse("78a3f80f-6dd4-4a38-b005-5178bc65f9cd")
        let timestamp = Instant.FromUtc(2026, 2, 19, 13, 0)

        let blobPath = Artifact.buildBlobPath timestamp artifactId

        Assert.That(blobPath, Is.EqualTo("grace-artifacts/2026/02/19/13/78a3f80f-6dd4-4a38-b005-5178bc65f9cd"))

    /// Verifies that deterministic Artifact Blob Path Uses By Id Partition.
    [<Test>]
    member _.DeterministicArtifactBlobPathUsesByIdPartition() =
        let artifactId = Guid.Parse("78a3f80f-6dd4-4a38-b005-5178bc65f9cd")

        let blobPath = Artifact.buildDeterministicBlobPath artifactId

        Assert.That(blobPath, Is.EqualTo("grace-artifacts/by-id/78a3f80f-6dd4-4a38-b005-5178bc65f9cd"))

    /// Verifies that deterministic Artifact Id Pins Known Vector.
    [<Test>]
    member _.DeterministicArtifactIdPinsKnownVector() =
        let artifactId = Artifact.createDeterministicArtifactId "promotion-set/validation/output"

        Assert.That(artifactId, Is.EqualTo(Guid.Parse("8b6502e5-4f50-524b-972e-1b932504e39d")))

    /// Verifies that deterministic Artifact Id Normalizes Empty And Whitespace Seeds.
    [<Test>]
    member _.DeterministicArtifactIdNormalizesEmptyAndWhitespaceSeeds() =
        let emptySeedArtifactId = Artifact.createDeterministicArtifactId String.Empty
        let whitespaceSeedArtifactId = Artifact.createDeterministicArtifactId "   "

        Assert.That(whitespaceSeedArtifactId, Is.EqualTo(emptySeedArtifactId))
        Assert.That(emptySeedArtifactId, Is.EqualTo(Guid.Parse("42c4b0e3-fc98-541c-9afb-f4c8996fb924")))

    /// Verifies that deterministic Artifact Id Normalizes Seed Case And Whitespace.
    [<Test>]
    member _.DeterministicArtifactIdNormalizesSeedCaseAndWhitespace() =
        let canonicalArtifactId = Artifact.createDeterministicArtifactId "artifact seed"
        let mixedArtifactId = Artifact.createDeterministicArtifactId " Artifact Seed "

        Assert.That(mixedArtifactId, Is.EqualTo(canonicalArtifactId))
        Assert.That(canonicalArtifactId, Is.EqualTo(Guid.Parse("d01d5ec6-e55b-5591-b80d-f0fcc46d8639")))

    /// Verifies that deterministic Artifact Id Pins Guid Version And Variant Bits.
    [<Test>]
    member _.DeterministicArtifactIdPinsGuidVersionAndVariantBits() =
        let artifactId = Artifact.createDeterministicArtifactId "promotion-set/validation/output"
        let formattedArtifactId = artifactId.ToString("D")

        Assert.That(formattedArtifactId.Substring(14, 1), Is.EqualTo("5"))
        Assert.That(formattedArtifactId.Substring(19, 1), Is.EqualTo("9"))
        Assert.That(artifactId, Is.EqualTo(Guid.Parse("8b6502e5-4f50-524b-972e-1b932504e39d")))

    /// Verifies that known Artifact Type Aliases Parse Case Insensitively.
    [<Test>]
    member _.KnownArtifactTypeAliasesParseCaseInsensitively() =
        let cases =
            [
                "agentsummary", ArtifactType.AgentSummary
                "CONFLICTREPORT", ArtifactType.ConflictReport
                "prompt", ArtifactType.Prompt
                "validationoutput", ArtifactType.ValidationOutput
                "reviewnotes", ArtifactType.ReviewNotes
                "other", ArtifactType.Other "Other"
            ]

        cases
        |> List.iter (fun (rawArtifactType, expected) ->
            let parsed = Artifact.parseArtifactType rawArtifactType

            Assert.That(parsed, Is.EqualTo(expected)))

    /// Verifies that unknown Artifact Type Maps To Other.
    [<Test>]
    member _.UnknownArtifactTypeMapsToOther() =
        let parsed = Artifact.parseArtifactType "CustomOutput"

        Assert.That(parsed, Is.EqualTo(ArtifactType.Other "CustomOutput"))

    /// Verifies that validation Result Requires Steps Computation Attempt When Promotion Set Scope Is Provided.
    [<Test>]
    member _.ValidationResultRequiresStepsComputationAttemptWhenPromotionSetScopeIsProvided() =
        let parameters = RecordValidationResultParameters()
        parameters.ValidationName <- "quick-scan"
        parameters.ValidationVersion <- "1.0"
        parameters.Status <- "Pass"
        parameters.PromotionSetId <- Guid.NewGuid().ToString()
        parameters.StepsComputationAttempt <- -1

        let firstError =
            ValidationResult.validationsForRecord parameters
            |> Utilities.getFirstError
            |> Async.AwaitTask
            |> Async.RunSynchronously

        Assert.That(firstError, Is.EqualTo(Some ValidationResultError.StepsComputationAttemptRequired))

    /// Verifies that validation Result Accepts Valid Promotion Set Scope.
    [<Test>]
    member _.ValidationResultAcceptsValidPromotionSetScope() =
        let parameters = RecordValidationResultParameters()
        parameters.ValidationName <- "quick-scan"
        parameters.ValidationVersion <- "1.0"
        parameters.Status <- "Pass"
        parameters.PromotionSetId <- Guid.NewGuid().ToString()
        parameters.StepsComputationAttempt <- 2

        let firstError =
            ValidationResult.validationsForRecord parameters
            |> Utilities.getFirstError
            |> Async.AwaitTask
            |> Async.RunSynchronously

        Assert.That(firstError, Is.EqualTo(None))
