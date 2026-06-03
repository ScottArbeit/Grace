namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared.Parameters.Validation
open Grace.Shared.Validation
open Grace.Shared.Validation.Errors
open Grace.Types.Artifact
open NodaTime
open NUnit.Framework
open System

[<Parallelizable(ParallelScope.All)>]
type ValidationArtifactContractTests() =

    [<Test>]
    member _.ArtifactBlobPathMatchesContract() =
        let artifactId = Guid.Parse("78a3f80f-6dd4-4a38-b005-5178bc65f9cd")
        let timestamp = Instant.FromUtc(2026, 2, 19, 13, 0)

        let blobPath = Artifact.buildBlobPath timestamp artifactId

        Assert.That(blobPath, Is.EqualTo("grace-artifacts/2026/02/19/13/78a3f80f-6dd4-4a38-b005-5178bc65f9cd"))

    [<Test>]
    member _.DeterministicArtifactBlobPathUsesByIdPartition() =
        let artifactId = Guid.Parse("78a3f80f-6dd4-4a38-b005-5178bc65f9cd")

        let blobPath = Artifact.buildDeterministicBlobPath artifactId

        Assert.That(blobPath, Is.EqualTo("grace-artifacts/by-id/78a3f80f-6dd4-4a38-b005-5178bc65f9cd"))

    [<Test>]
    member _.DeterministicArtifactIdPinsKnownVector() =
        let artifactId = Artifact.createDeterministicArtifactId "promotion-set/validation/output"

        Assert.That(artifactId, Is.EqualTo(Guid.Parse("8b6502e5-4f50-d25b-972e-1b932504e39d")))

    [<Test>]
    member _.DeterministicArtifactIdNormalizesEmptyAndWhitespaceSeeds() =
        let emptySeedArtifactId = Artifact.createDeterministicArtifactId String.Empty
        let whitespaceSeedArtifactId = Artifact.createDeterministicArtifactId "   "

        Assert.That(whitespaceSeedArtifactId, Is.EqualTo(emptySeedArtifactId))
        Assert.That(emptySeedArtifactId, Is.EqualTo(Guid.Parse("42c4b0e3-fc98-145c-9afb-f4c8996fb924")))

    [<Test>]
    member _.DeterministicArtifactIdNormalizesSeedCaseAndWhitespace() =
        let canonicalArtifactId = Artifact.createDeterministicArtifactId "artifact seed"
        let mixedArtifactId = Artifact.createDeterministicArtifactId " Artifact Seed "

        Assert.That(mixedArtifactId, Is.EqualTo(canonicalArtifactId))
        Assert.That(canonicalArtifactId, Is.EqualTo(Guid.Parse("d01d5ec6-e55b-1551-b80d-f0fcc46d8639")))

    [<Test>]
    member _.DeterministicArtifactIdPinsGuidVersionAndVariantBits() =
        let artifactId = Artifact.createDeterministicArtifactId "AGENTSUMMARY:42"
        let guidBytes = artifactId.ToByteArray()

        Assert.That(guidBytes[6] &&& 0xF0uy, Is.EqualTo(0x50uy))
        Assert.That(guidBytes[8] &&& 0xC0uy, Is.EqualTo(0x80uy))
        Assert.That(artifactId, Is.EqualTo(Guid.Parse("d091d3f6-ac6b-5259-9905-ab9949092970")))

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

    [<Test>]
    member _.UnknownArtifactTypeMapsToOther() =
        let parsed = Artifact.parseArtifactType "CustomOutput"

        Assert.That(parsed, Is.EqualTo(ArtifactType.Other "CustomOutput"))

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
