namespace Grace.Types.Tests

open Grace.Shared.Utilities
open Grace.Types.Common
open NodaTime
open NUnit.Framework
open System.Collections.Generic

[<TestFixture>]
type EventMetadataTypesTests() =

    [<Test>]
    member _.NewLeavesClientTypeUnsetForInternalMetadata() =
        let metadata = EventMetadata.New "corr-internal" "GraceSystem"

        Assert.That(metadata.CorrelationId, Is.EqualTo("corr-internal"))
        Assert.That(metadata.Principal, Is.EqualTo("GraceSystem"))
        Assert.That(metadata.ClientType, Is.EqualTo(Microsoft.FSharp.Core.Option.None))

    [<Test>]
    member _.ClientTypeRoundTripsThroughGraceJsonSerialization() =
        let metadata =
            {
                Timestamp = Instant.FromUtc(2026, 5, 21, 12, 0)
                CorrelationId = "corr-cli"
                Principal = "tester"
                ClientType = Some(ClientType.CLI "0.1.2.3")
                Properties = Dictionary<string, string>()
            }

        let roundTrip =
            metadata
            |> serialize
            |> deserialize<EventMetadata>

        match roundTrip.ClientType with
        | Some (ClientType.CLI version) -> Assert.That(version, Is.EqualTo("0.1.2.3"))
        | other -> Assert.Fail($"Expected CLI client type, got {other}.")
