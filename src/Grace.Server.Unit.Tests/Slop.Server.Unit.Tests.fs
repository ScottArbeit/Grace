namespace Grace.Server.Tests

open Grace.Shared
open Grace.Types.Owner
open NUnit.Framework
open System
open System.Text.Json

/// Covers slop Unit behavior in no-Aspire server unit tests.
[<TestFixture>]
type SlopUnit() =

    // Slop guard: if JSON serialization options change, round-tripping breaks.
    /// Verifies that owner Dto Json Round Trip.
    [<Test; Category("Slop")>]
    member _.OwnerDtoJsonRoundTrip() =
        let original = { OwnerDto.Default with OwnerId = Guid.NewGuid(); OwnerName = "SlopOwner" }

        let json = JsonSerializer.Serialize(original, Constants.JsonSerializerOptions)
        let roundTrip = JsonSerializer.Deserialize<OwnerDto>(json, Constants.JsonSerializerOptions)
        Assert.That(roundTrip, Is.EqualTo(original))
