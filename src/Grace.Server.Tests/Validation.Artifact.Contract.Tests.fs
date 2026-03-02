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
