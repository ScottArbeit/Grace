namespace Grace.Types.Tests

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Usage
open NodaTime
open NUnit.Framework
open System
open System.IO
open System.Text.Json

/// Contains usage fact test helpers.
module UsageFactTestData =

    /// Provides a deterministic usage fact identifier that differs from the correlation id.
    let usageFactId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")

    /// Provides the deterministic correlation id carried beside the fact identifier.
    let correlationId = CorrelationId "corr-usage-tracer-bullet"

    /// Provides the deterministic owner id for repository-scoped usage facts.
    let ownerId = OwnerId.Parse("11111111-1111-1111-1111-111111111111")

    /// Provides the deterministic organization id for repository-scoped usage facts.
    let organizationId = OrganizationId.Parse("22222222-2222-2222-2222-222222222222")

    /// Provides the deterministic repository id for repository-scoped usage facts.
    let repositoryId = RepositoryId.Parse("33333333-3333-3333-3333-333333333333")

    /// Provides the deterministic storage pool id for repository storage facts.
    let storagePoolId = StoragePoolId "storage-pool-main"

    /// Provides an observation instant with seconds that should normalize into the minute bucket.
    let observedAtWithSeconds = Instant.FromUtc(2026, 7, 4, 12, 34, 56)

    /// Provides the expected minute bucket for the deterministic observation instant.
    let observedAtMinute = Instant.FromUtc(2026, 7, 4, 12, 34)

    /// Builds a valid repository storage fact for serialization and validation tests.
    let validFact () =
        UsageFact.RepositoryStorageBytesMinute(usageFactId, correlationId, ownerId, organizationId, repositoryId, storagePoolId, 4096L, observedAtWithSeconds)

/// Contains tests covering usage fact contract behavior.
[<Parallelizable(ParallelScope.All)>]
type UsageFactContractTests() =

    /// Reads a required JSON property from the serialized contract shape.
    let property (document: JsonDocument) (name: string) = document.RootElement.GetProperty(name)

    /// Reads a JSON property as a string for deterministic shape assertions.
    let stringProperty (document: JsonDocument) (name: string) = (property document name).GetString()

    /// Reads a JSON property as a guid for deterministic shape assertions.
    let guidProperty (document: JsonDocument) (name: string) = (property document name).GetGuid()

    /// Asserts that usage fact validation fails with a message containing the expected text.
    let assertInvalid expected (fact: UsageFact) =
        match UsageFact.Validate fact with
        | Ok () -> Assert.Fail($"Usage fact should fail validation with '{expected}'.")
        | Error errors -> Assert.That(errors, Has.Some.Contains(expected))

    /// Builds the deterministic usage fact fixture for a supported v1 fact kind.
    let supportedV1Fact kind =
        match kind with
        | UsageFactKind.RepositoryStorageBytesMinute -> UsageFactTestData.validFact ()
        | _ -> invalidArg (nameof kind) $"Unsupported v1 UsageFactKind '{kind}'."

    /// Finds the golden JSON fixture for a supported v1 fact kind.
    let goldenFixturePath kind =
        match kind with
        | UsageFactKind.RepositoryStorageBytesMinute ->
            Path.Combine(__SOURCE_DIRECTORY__, "Fixtures", "UsageFacts", "v1", "repository-storage-bytes-minute.json")
        | _ -> invalidArg (nameof kind) $"Unsupported v1 UsageFactKind '{kind}'."

    /// Normalizes newline and final-newline differences before comparing golden JSON fixtures.
    let normalizeJsonText (json: string) = json.ReplaceLineEndings("\n").TrimEnd()

    /// Verifies that every supported v1 fact kind has a stable golden JSON fixture.
    [<Test>]
    member _.SupportedV1UsageFactKindsHaveGoldenJsonFixtures() =
        Assert.That(
            UsageFact.SupportedV1FactKinds,
            Is.EquivalentTo(
                [|
                    UsageFactKind.RepositoryStorageBytesMinute
                |]
            )
        )

        for factKind in UsageFact.SupportedV1FactKinds do
            let fact = supportedV1Fact factKind
            let actual = serialize fact
            let expected = File.ReadAllText(goldenFixturePath factKind)

            Assert.That(normalizeJsonText actual, Is.EqualTo(normalizeJsonText expected), $"Golden JSON drifted for {factKind}.")

            let roundTrip = deserialize<UsageFact> expected

            match UsageFact.Validate roundTrip with
            | Ok () -> Assert.That(roundTrip, Is.EqualTo(fact), $"Golden JSON no longer round trips for {factKind}.")
            | Error errors ->
                let errorText = String.Join("; ", errors)
                Assert.Fail($"Golden JSON for {factKind} should validate, but failed with: {errorText}")

    /// Verifies that a valid repository storage fact serializes with the expected JSON field names and values.
    [<Test>]
    member _.RepositoryStorageUsageFactSerializesWithExpectedJsonShape() =
        let fact = UsageFactTestData.validFact ()
        let json = serialize fact

        use document = JsonDocument.Parse(json)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(stringProperty document "Class", Is.EqualTo(nameof UsageFact))
                Assert.That((property document "SchemaVersion").GetInt32(), Is.EqualTo(UsageFactSchemaVersion))
                Assert.That(guidProperty document "UsageFactId", Is.EqualTo(UsageFactTestData.usageFactId))
                Assert.That(stringProperty document "CorrelationId", Is.EqualTo(UsageFactTestData.correlationId))
                Assert.That(stringProperty document "FactKind", Is.EqualTo("repositoryStorageBytesMinute"))

                Assert.That(
                    (property document "Scope")
                        .GetProperty("OwnerId")
                        .GetGuid(),
                    Is.EqualTo(UsageFactTestData.ownerId)
                )

                Assert.That(
                    (property document "Scope")
                        .GetProperty("OrganizationId")
                        .GetGuid(),
                    Is.EqualTo(UsageFactTestData.organizationId)
                )

                Assert.That(
                    (property document "Scope")
                        .GetProperty("RepositoryId")
                        .GetGuid(),
                    Is.EqualTo(UsageFactTestData.repositoryId)
                )

                Assert.That(
                    (property document "Resource")
                        .GetProperty("StoragePoolId")
                        .GetString(),
                    Is.EqualTo(UsageFactTestData.storagePoolId)
                )

                Assert.That(stringProperty document "Source", Is.EqualTo(DefaultUsageFactSource))
                Assert.That(stringProperty document "Confidence", Is.EqualTo("observed"))
                Assert.That((property document "Quantity").GetInt64(), Is.EqualTo(4096L))
                Assert.That(stringProperty document "ObservedAt", Is.EqualTo("2026-07-04T12:34:00Z")))
        )

    /// Verifies that a valid repository storage fact round trips through Grace JSON serialization.
    [<Test>]
    member _.RepositoryStorageUsageFactRoundTripsThroughJson() =
        let fact = UsageFactTestData.validFact ()
        let roundTrip = deserialize<UsageFact> (serialize fact)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(roundTrip, Is.EqualTo(fact))

                match UsageFact.Validate roundTrip with
                | Ok () -> ()
                | Error errors ->
                    let errorText = String.Join("; ", errors)
                    Assert.Fail($"Round-tripped usage fact should be valid, but failed with: {errorText}"))
        )

    /// Verifies that fact identity and request correlation remain separate serialized fields.
    [<Test>]
    member _.UsageFactIdAndCorrelationIdRemainSeparateSerializedFields() =
        let fact = UsageFactTestData.validFact ()

        use document = JsonDocument.Parse(serialize fact)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(stringProperty document "UsageFactId", Is.EqualTo("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))
                Assert.That(stringProperty document "CorrelationId", Is.EqualTo("corr-usage-tracer-bullet"))
                Assert.That(stringProperty document "UsageFactId", Is.Not.EqualTo(stringProperty document "CorrelationId")))
        )

    /// Verifies that default identity fields fail validation instead of becoming valid facts.
    [<Test>]
    member _.DefaultRequiredIdentityFieldsFailValidationClearly() =
        let fact =
            { UsageFactTestData.validFact () with
                UsageFactId = UsageFactId.Empty
                Scope = { OwnerId = OwnerId.Empty; OrganizationId = OrganizationId.Empty; RepositoryId = RepositoryId.Empty }
            }

        match UsageFact.Validate fact with
        | Ok () -> Assert.Fail("Usage fact should fail validation when required identity fields are missing.")
        | Error errors ->
            Assert.Multiple(
                Action (fun () ->
                    Assert.That(errors, Has.Some.Contains("UsageFactId is required."))
                    Assert.That(errors, Has.Some.Contains("Scope.OwnerId is required."))
                    Assert.That(errors, Has.Some.Contains("Scope.OrganizationId is required."))
                    Assert.That(errors, Has.Some.Contains("Scope.RepositoryId is required.")))
            )

    /// Verifies that unsupported schema versions fail validation instead of becoming accepted contract drift.
    [<TestCase(0)>]
    [<TestCase(2)>]
    member _.UnsupportedSchemaVersionFailsValidationClearly(schemaVersion: int) =
        let fact = { UsageFactTestData.validFact () with SchemaVersion = schemaVersion }

        assertInvalid $"SchemaVersion '{schemaVersion}' is not supported. Expected '{UsageFactSchemaVersion}'." fact

    /// Verifies that serialized future schema versions deserialize but still fail contract validation.
    [<Test>]
    member _.UnsupportedSchemaVersionJsonFailsValidationClearly() =
        let futureSchemaJson =
            File
                .ReadAllText(goldenFixturePath UsageFactKind.RepositoryStorageBytesMinute)
                .Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 2")

        let fact = deserialize<UsageFact> futureSchemaJson

        assertInvalid $"SchemaVersion '2' is not supported. Expected '{UsageFactSchemaVersion}'." fact

    /// Verifies that missing identity JSON fields fail before becoming a partial contract value.
    [<Test>]
    member _.MissingRequiredIdentityJsonFieldsFailDeserializationClearly() =
        let missingIdentityJson =
            """
{
  "Class": "UsageFact",
  "SchemaVersion": 1,
  "CorrelationId": "corr-usage-tracer-bullet",
  "FactKind": "repositoryStorageBytesMinute",
  "Scope": {},
  "Resource": {
    "StoragePoolId": "storage-pool-main"
  },
  "Source": "Grace.Server",
  "Confidence": "observed",
  "Quantity": 4096,
  "ObservedAt": "2026-07-04T12:34:00Z"
}
"""

        let ex =
            Assert.Throws<JsonException>(
                Action (fun () ->
                    deserialize<UsageFact> missingIdentityJson
                    |> ignore)
            )

        Assert.That(
            ex.Message,
            Does
                .Contain("Missing field")
                .And.Contains("OwnerId")
        )

    /// Verifies that malformed identity JSON fails before becoming a contract value.
    [<Test>]
    member _.MalformedIdentityJsonFailsDeserializationClearly() =
        let malformedJson =
            """
{
  "Class": "UsageFact",
  "SchemaVersion": 1,
  "UsageFactId": "not-a-guid",
  "CorrelationId": "corr-usage-tracer-bullet",
  "FactKind": "repositoryStorageBytesMinute",
  "Scope": {
    "OwnerId": "11111111-1111-1111-1111-111111111111",
    "OrganizationId": "22222222-2222-2222-2222-222222222222",
    "RepositoryId": "33333333-3333-3333-3333-333333333333"
  },
  "Resource": {
    "StoragePoolId": "storage-pool-main"
  },
  "Source": "Grace.Server",
  "Confidence": "observed",
  "Quantity": 4096,
  "ObservedAt": "2026-07-04T12:34:00Z"
}
"""

        let ex = Assert.Throws<JsonException>(Action(fun () -> deserialize<UsageFact> malformedJson |> ignore))
        Assert.That(ex.Message, Does.Contain("not-a-guid").Or.Contains("Guid"))

    /// Verifies that unsupported future fact kind values are not accepted as known facts.
    [<TestCase(0)>]
    [<TestCase(999)>]
    member _.UnsupportedFutureFactKindValueFailsValidationClearly(factKind: int) =
        let fact = { UsageFactTestData.validFact () with FactKind = enum<UsageFactKind> factKind }

        assertInvalid $"FactKind '{factKind}' is not supported." fact

    /// Verifies that unknown future fact kind strings do not deserialize as known facts.
    [<Test>]
    member _.UnknownFutureFactKindStringFailsDeserializationClearly() =
        let unknownKindJson =
            (serialize (UsageFactTestData.validFact ()))
                .Replace("repositoryStorageBytesMinute", "futureUsageFactKind")

        let ex = Assert.Throws<JsonException>(Action(fun () -> deserialize<UsageFact> unknownKindJson |> ignore))

        Assert.That(
            ex.Message,
            Does
                .Contain("futureUsageFactKind")
                .Or.Contains("UsageFactKind")
        )

    /// Verifies that missing source values fail validation instead of entering the durable fact stream.
    [<TestCase("")>]
    [<TestCase("   ")>]
    member _.MissingSourceFailsValidationClearly(source: string) =
        let fact = { UsageFactTestData.validFact () with Source = source }

        assertInvalid "Source is required." fact

    /// Verifies that unknown confidence values fail validation instead of weakening fact evidence semantics.
    [<TestCase(0)>]
    [<TestCase(2)>]
    [<TestCase(999)>]
    member _.UnsupportedConfidenceFailsValidationClearly(confidence: int) =
        let fact = { UsageFactTestData.validFact () with Confidence = enum<UsageFactConfidence> confidence }

        assertInvalid $"Confidence '{confidence}' is not supported." fact

    /// Verifies that zero and negative quantities are rejected by the contract validator.
    [<TestCase(0L)>]
    [<TestCase(-1L)>]
    member _.ZeroAndNegativeQuantitiesFailValidationClearly(quantity: int64) =
        let fact = { UsageFactTestData.validFact () with Quantity = quantity }

        assertInvalid "Quantity must be greater than zero." fact

    /// Verifies that observation timestamps normalize to minute buckets during construction.
    [<Test>]
    member _.RepositoryStorageFactCreationNormalizesObservedAtToMinuteBoundary() =
        let fact = UsageFactTestData.validFact ()

        Assert.That(fact.ObservedAt, Is.EqualTo(UsageFactTestData.observedAtMinute))

    /// Verifies that non-normalized observation timestamps fail validation when bypassing the constructor.
    [<Test>]
    member _.NonMinuteBoundaryObservedAtFailsValidationClearly() =
        let fact = { UsageFactTestData.validFact () with ObservedAt = UsageFactTestData.observedAtWithSeconds }

        assertInvalid "ObservedAt must be normalized to a UTC minute boundary." fact

    /// Verifies that default observation timestamps fail validation instead of becoming accepted usage evidence.
    [<Test>]
    member _.DefaultObservedAtFailsValidationClearly() =
        let fact = { UsageFactTestData.validFact () with ObservedAt = Constants.DefaultTimestamp }

        assertInvalid "ObservedAt is required." fact

    /// Verifies that null root fact values become validation errors instead of a null dereference.
    [<Test>]
    member _.NullRootFactFailsValidationClearly() =
        let fact = Unchecked.defaultof<UsageFact>

        Assert.DoesNotThrow(Action(fun () -> UsageFact.Validate fact |> ignore))

        match UsageFact.Validate fact with
        | Ok () -> Assert.Fail("Usage fact should fail validation when the fact root is null.")
        | Error errors -> Assert.That(errors, Has.Some.Contains("UsageFact is required."))

    /// Verifies that null nested scope values become validation errors instead of a null dereference.
    [<Test>]
    member _.NullScopeFailsValidationClearly() =
        let fact = { UsageFactTestData.validFact () with Scope = Unchecked.defaultof<UsageFactScope> }

        Assert.DoesNotThrow(Action(fun () -> UsageFact.Validate fact |> ignore))

        match UsageFact.Validate fact with
        | Ok () -> Assert.Fail("Usage fact should fail validation when Scope is null.")
        | Error errors -> Assert.That(errors, Has.Some.Contains("Scope is required."))

    /// Verifies that null nested resource values become validation errors instead of a null dereference.
    [<Test>]
    member _.NullResourceFailsValidationClearly() =
        let fact = { UsageFactTestData.validFact () with Resource = Unchecked.defaultof<UsageFactResource> }

        Assert.DoesNotThrow(Action(fun () -> UsageFact.Validate fact |> ignore))

        match UsageFact.Validate fact with
        | Ok () -> Assert.Fail("Usage fact should fail validation when Resource is null.")
        | Error errors -> Assert.That(errors, Has.Some.Contains("Resource is required."))
