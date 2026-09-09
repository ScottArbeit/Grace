namespace Grace.Types.Tests

open System
open System.Text.Json
open Grace.Shared
open Grace.Types.UsageObservation
open NodaTime.Text
open NUnit.Framework

/// Exercises the separate source observation contracts independently of storage and hosting.
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

    let textObservation =
        {
            ObservationId = Guid.Parse "10560000-0000-0000-0000-000000000001"
            Scope =
                {
                    OwnerId = Guid.Parse "11111111-1111-1111-1111-111111111111"
                    OrganizationId = Guid.Parse "22222222-2222-2222-2222-222222222222"
                    RepositoryId = Guid.Parse "33333333-3333-3333-3333-333333333333"
                }
            DeclaredTextContentUtf8Bytes = 0L
            DistinctTextContentCount = 0L
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

    /// Supplies a deterministic Artifact declaration and complete nanosecond window.
    let artifactObservation =
        {
            ObservationId = Guid.Parse "10560000-0000-0000-0000-000000000001"
            Scope =
                {
                    OwnerId = Guid.Parse "11111111-1111-1111-1111-111111111111"
                    OrganizationId = Guid.Parse "22222222-2222-2222-2222-222222222222"
                    RepositoryId = Guid.Parse "33333333-3333-3333-3333-333333333333"
                }
            DeclaredArtifactBytes = 0L
            DistinctArtifactCount = 0L
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

    /// Accepts known zero and positive completed readings while preserving every nanosecond in real Grace JSON.
    [<Test>]
    member _.``text zero positive and precise windows round trip through Grace JSON``() =
        [
            textObservation
            { textObservation with DeclaredTextContentUtf8Bytes = 37L; DistinctTextContentCount = 2L }
        ]
        |> List.iter (fun value ->
            Assert.That(
                TextContentSizeObservation.Validate value
                |> Result.isOk,
                Is.True
            )

            let json = JsonSerializer.Serialize(value, Constants.JsonSerializerOptions)
            use document = JsonDocument.Parse json

            Assert.That(
                document
                    .RootElement
                    .GetProperty("DeclaredTextContentUtf8Bytes")
                    .GetInt64(),
                Is.EqualTo value.DeclaredTextContentUtf8Bytes
            )

            Assert.That(
                document
                    .RootElement
                    .GetProperty("DistinctTextContentCount")
                    .GetInt64(),
                Is.EqualTo value.DistinctTextContentCount
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

            Assert.That(JsonSerializer.Deserialize<TextContentSizeObservation>(json, Constants.JsonSerializerOptions), Is.EqualTo value))

    /// Rejects each independently incomplete identity, negative quantity and missing or reversed read window.
    [<Test>]
    member _.``invalid text observation partitions are rejected``() =
        [
            Unchecked.defaultof<TextContentSizeObservation>
            { textObservation with ObservationId = Guid.Empty }
            { textObservation with Scope = Unchecked.defaultof<_> }
            { textObservation with Scope = { textObservation.Scope with OwnerId = Guid.Empty } }
            { textObservation with Scope = { textObservation.Scope with OrganizationId = Guid.Empty } }
            { textObservation with Scope = { textObservation.Scope with RepositoryId = Guid.Empty } }
            { textObservation with DeclaredTextContentUtf8Bytes = -1L }
            { textObservation with DistinctTextContentCount = -1L }
            { textObservation with EnumerationStartedAt = Constants.DefaultTimestamp }
            { textObservation with EnumerationFinishedAt = Constants.DefaultTimestamp }
            { textObservation with
                EnumerationFinishedAt =
                    textObservation.EnumerationStartedAt
                    - NodaTime.Duration.FromNanoseconds 1L
            }
        ]
        |> List.iter (fun value ->
            Assert.That(
                TextContentSizeObservation.Validate value
                |> Result.isError,
                Is.True
            ))

    /// Accepts known zero and positive completed readings while preserving every nanosecond in real Grace JSON.
    [<Test>]
    member _.``artifact zero positive and precise windows round trip through Grace JSON``() =
        [
            artifactObservation
            { artifactObservation with DeclaredArtifactBytes = 37L; DistinctArtifactCount = 2L }
        ]
        |> List.iter (fun value ->
            Assert.That(
                ArtifactSizeObservation.Validate value
                |> Result.isOk,
                Is.True
            )

            let json = JsonSerializer.Serialize(value, Constants.JsonSerializerOptions)
            use document = JsonDocument.Parse json

            Assert.That(
                document
                    .RootElement
                    .GetProperty("DeclaredArtifactBytes")
                    .GetInt64(),
                Is.EqualTo value.DeclaredArtifactBytes
            )

            Assert.That(
                document
                    .RootElement
                    .GetProperty("DistinctArtifactCount")
                    .GetInt64(),
                Is.EqualTo value.DistinctArtifactCount
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

            Assert.That(JsonSerializer.Deserialize<ArtifactSizeObservation>(json, Constants.JsonSerializerOptions), Is.EqualTo value))

    /// Rejects each independently incomplete identity, negative quantity and missing or reversed read window.
    [<Test>]
    member _.``invalid artifact observation partitions are rejected``() =
        [
            Unchecked.defaultof<ArtifactSizeObservation>
            { artifactObservation with ObservationId = Guid.Empty }
            { artifactObservation with Scope = Unchecked.defaultof<_> }
            { artifactObservation with Scope = { artifactObservation.Scope with OwnerId = Guid.Empty } }
            { artifactObservation with Scope = { artifactObservation.Scope with OrganizationId = Guid.Empty } }
            { artifactObservation with Scope = { artifactObservation.Scope with RepositoryId = Guid.Empty } }
            { artifactObservation with DeclaredArtifactBytes = -1L }
            { artifactObservation with DistinctArtifactCount = -1L }
            { artifactObservation with EnumerationStartedAt = Constants.DefaultTimestamp }
            { artifactObservation with EnumerationFinishedAt = Constants.DefaultTimestamp }
            { artifactObservation with
                EnumerationFinishedAt =
                    artifactObservation.EnumerationStartedAt
                    - NodaTime.Duration.FromNanoseconds 1L
            }
        ]
        |> List.iter (fun value ->
            Assert.That(
                ArtifactSizeObservation.Validate value
                |> Result.isError,
                Is.True
            ))
