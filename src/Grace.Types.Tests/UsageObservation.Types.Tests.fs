namespace Grace.Types.Tests

open System
open System.Text.Json
open Grace.Shared
open Grace.Types.UsageObservation
open NodaTime.Text
open NUnit.Framework

/// Exercises the single completed-observation contract independently of storage and hosting.
type UsageObservationTests() =
    let observation =
        {
            ObservationId = Guid.Parse "10560000-0000-0000-0000-000000000001"
            Scope =
                {
                    OwnerId = Guid.Parse "11111111-1111-1111-1111-111111111111"
                    OrganizationId = Guid.Parse "22222222-2222-2222-2222-222222222222"
                    RepositoryId = Guid.Parse "33333333-3333-3333-3333-333333333333"
                }
            DeclaredLogicalBytes = 0L
            DistinctContentCount = 0L
            EnumerationStartedAt =
                InstantPattern
                    .ExtendedIso
                    .Parse(
                        "2026-09-07T01:02:03.123456789Z"
                    )
                    .Value
            EnumerationFinishedAt =
                InstantPattern
                    .ExtendedIso
                    .Parse(
                        "2026-09-07T01:02:03.123456790Z"
                    )
                    .Value
        }

    /// Accepts known zero and positive completed readings while preserving every nanosecond in real Grace JSON.
    [<Test>]
    member _.``zero positive and precise windows round trip through Grace JSON``() =
        [
            observation
            { observation with DeclaredLogicalBytes = 37L; DistinctContentCount = 2L }
        ]
        |> List.iter (fun value ->
            Assert.That(
                DirectoryVersionSizeObservation.Validate value
                |> Result.isOk,
                Is.True
            )

            let json = JsonSerializer.Serialize(value, Constants.JsonSerializerOptions)
            use document = JsonDocument.Parse json

            Assert.That(
                document
                    .RootElement
                    .GetProperty("DeclaredLogicalBytes")
                    .GetInt64(),
                Is.EqualTo value.DeclaredLogicalBytes
            )

            Assert.That(
                document
                    .RootElement
                    .GetProperty("DistinctContentCount")
                    .GetInt64(),
                Is.EqualTo value.DistinctContentCount
            )

            Assert.That(
                document
                    .RootElement
                    .GetProperty("EnumerationStartedAt")
                    .GetString(),
                Is.EqualTo "2026-09-07T01:02:03.123456789Z"
            )

            Assert.That(
                document
                    .RootElement
                    .GetProperty("EnumerationFinishedAt")
                    .GetString(),
                Is.EqualTo "2026-09-07T01:02:03.12345679Z"
            )

            Assert.That(JsonSerializer.Deserialize<DirectoryVersionSizeObservation>(json, Constants.JsonSerializerOptions), Is.EqualTo value))

    /// Rejects each independently incomplete identity, negative quantity and missing or reversed read window.
    [<Test>]
    member _.``invalid observation partitions are rejected``() =
        [
            Unchecked.defaultof<DirectoryVersionSizeObservation>
            { observation with ObservationId = Guid.Empty }
            { observation with Scope = Unchecked.defaultof<_> }
            { observation with Scope = { observation.Scope with OwnerId = Guid.Empty } }
            { observation with Scope = { observation.Scope with OrganizationId = Guid.Empty } }
            { observation with Scope = { observation.Scope with RepositoryId = Guid.Empty } }
            { observation with DeclaredLogicalBytes = -1L }
            { observation with DistinctContentCount = -1L }
            { observation with EnumerationStartedAt = Constants.DefaultTimestamp }
            { observation with EnumerationFinishedAt = Constants.DefaultTimestamp }
            { observation with
                EnumerationFinishedAt =
                    observation.EnumerationStartedAt
                    - NodaTime.Duration.FromNanoseconds 1L
            }
        ]
        |> List.iter (fun value ->
            Assert.That(
                DirectoryVersionSizeObservation.Validate value
                |> Result.isError,
                Is.True
            ))
