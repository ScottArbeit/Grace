namespace Grace.Types.Tests

open Grace.Types.Common
open Grace.Types.Validation
open NUnit.Framework
open NodaTime
open System
open System.Collections.Generic

/// Contains tests covering validation quick scan determinism behavior.
[<Parallelizable(ParallelScope.All)>]
type ValidationQuickScanDeterminism() =
    /// Exercises metadata coverage for the types validation contract.
    let metadata timestamp =
        {
            Timestamp = timestamp
            CorrelationId = "corr-1"
            Principal = "tester"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = Dictionary<string, string>()
        }

    /// Verifies that update uses event payload and timestamp.
    [<Test>]
    member _.UpdateUsesEventPayloadAndTimestamp() =
        let timestamp = Instant.FromUtc(2025, 1, 1, 0, 0)
        let validationResultId = Guid.NewGuid()

        let validationResult =
            { ValidationResultDto.Default with
                ValidationResultId = validationResultId
                ValidationName = "quick-scan"
                ValidationVersion = "1.0"
                Output = { Status = ValidationStatus.Pass; Summary = "quick-scan complete"; ArtifactIds = [] }
                CreatedAt = timestamp
            }

        let validationEvent: ValidationResultEvent = { Event = ValidationResultEventType.Recorded validationResult; Metadata = metadata timestamp }

        let updated = ValidationResultDto.UpdateDto validationEvent ValidationResultDto.Default

        Assert.That(updated.ValidationResultId, Is.EqualTo(validationResult.ValidationResultId))
        Assert.That(updated.ValidationName, Is.EqualTo(validationResult.ValidationName))
        Assert.That(updated.Output, Is.EqualTo(validationResult.Output))
        Assert.That(updated.UpdatedAt, Is.EqualTo(Some timestamp))
        Assert.That(updated.OnBehalfOf, Is.EquivalentTo([ UserId "tester" ]))

    /// Verifies that update is deterministic for same event.
    [<Test>]
    member _.UpdateIsDeterministicForSameEvent() =
        let timestamp = Instant.FromUtc(2025, 2, 1, 0, 0)
        let validationResultId = Guid.NewGuid()

        let validationResult =
            { ValidationResultDto.Default with
                ValidationResultId = validationResultId
                ValidationName = "quick-scan"
                ValidationVersion = "1.0"
                Output = { Status = ValidationStatus.Pass; Summary = "quick-scan complete"; ArtifactIds = [] }
                CreatedAt = timestamp
            }

        let validationEvent: ValidationResultEvent = { Event = ValidationResultEventType.Recorded validationResult; Metadata = metadata timestamp }

        let first = ValidationResultDto.UpdateDto validationEvent ValidationResultDto.Default
        let second = ValidationResultDto.UpdateDto validationEvent ValidationResultDto.Default

        Assert.That(first, Is.EqualTo(second))
