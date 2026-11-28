namespace Grace.Types.Tests

open Grace.Shared
open Grace.Types.Common
open Grace.Types.RepositoryContentCounter
open Grace.Types.Usage
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Text.Json

/// Covers the counter's storage meter changes and the public storage usage answer.
[<Parallelizable(ParallelScope.All)>]
type UsageTypesTests() =
    let repositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let storagePoolId = "pool-a"
    let manifestAddress = "manifest-a"

    /// Builds event metadata for one counter event.
    let metadata =
        { Timestamp = Instant.FromUnixTimeSeconds 1L; CorrelationId = "corr"; Principal = "test"; ClientType = None; Properties = Dictionary<string, string>() }

    let added operationId size =
        { Event = RepositoryContentCounterEventType.ReferenceAdded(operationId, repositoryId, storagePoolId, manifestAddress, size); Metadata = metadata }

    let removed operationId = { Event = RepositoryContentCounterEventType.ReferenceRemoved operationId; Metadata = metadata }

    let apply events =
        events
        |> List.fold (fun counter event -> RepositoryContentCounterDto.UpdateDto event counter) RepositoryContentCounterDto.Default

    /// Keeps record the meter change with the count, and a second reference to the same content adds nothing.
    [<Test>]
    member _.OnlyZeroCrossingsRecordMeterChanges() =
        let counter =
            apply [ added "a" 2765L
                    added "b" 2765L
                    removed "c"
                    removed "d"
                    added "e" 2765L ]

        Assert.That(
            counter.PendingMeterChanges,
            Is.EqualTo(
                [
                    { Revision = 1L; LogicalBytesDelta = 2765L }
                    { Revision = 4L; LogicalBytesDelta = -2765L }
                    { Revision = 5L; LogicalBytesDelta = 2765L }
                ]
            )
        )

    /// A release subtracts the size recorded by the first add, whatever a later add claims.
    [<Test>]
    member _.ReleaseSubtractsTheRecordedSize() =
        let counter =
            apply [ added "a" 100L
                    added "b" 999L
                    removed "c"
                    removed "d" ]

        Assert.That(counter.LogicalSize, Is.EqualTo 100L)

        Assert.That(
            counter.PendingMeterChanges
            |> List.sumBy (fun change -> change.LogicalBytesDelta),
            Is.EqualTo 0L
        )

    /// The answer lists every class, counts only manifest-backed files, keeps zero as a number, and says it is partial.
    [<Test>]
    member _.UsageJsonKeepsZeroAndNotCountedExplicit() =
        let scope = { OwnerId = Guid.NewGuid(); OrganizationId = Guid.NewGuid(); RepositoryId = repositoryId }

        let usage =
            RepositoryStorageUsage.Create
                scope
                (Instant.FromUnixTimeSeconds 0L)
                [
                    StorageContentClass.ManifestBackedFiles, 0L
                ]

        use document = JsonDocument.Parse(JsonSerializer.Serialize(usage, Constants.JsonSerializerOptions))
        let root = document.RootElement

        Assert.That(root.GetProperty("Coverage").GetString(), Is.EqualTo "partial")
        Assert.That(root.TryGetProperty("TotalBytes") |> fst, Is.False)

        let classes =
            root
                .GetProperty("ContentClasses")
                .EnumerateArray()
            |> Seq.toList

        Assert.That(
            classes
            |> List.map (fun item -> item.GetProperty("ContentClass").GetString()),
            Is.EqualTo [ "manifestBackedFiles"
                         "wholeFiles"
                         "workItemText"
                         "artifactPayloads" ]
        )

        Assert.That(classes[ 0 ].GetProperty("Counted").GetBoolean(), Is.True)

        Assert.That(
            classes[0]
                .GetProperty("RetainedLogicalBytes")
                .GetInt64(),
            Is.EqualTo 0L
        )

        for item in classes.Tail do
            Assert.That(item.GetProperty("Counted").GetBoolean(), Is.False)
            Assert.That(item.GetProperty("RetainedLogicalBytes").ValueKind, Is.EqualTo JsonValueKind.Null)

        let roundTripped = JsonSerializer.Deserialize<RepositoryStorageUsage>(root.GetRawText(), Constants.JsonSerializerOptions)
        Assert.That(roundTripped, Is.EqualTo usage)

    /// Coverage becomes complete only when every class is counted.
    [<Test>]
    member _.CoverageIsCompleteOnlyWhenEveryClassIsCounted() =
        let all =
            RepositoryStorageUsage.AllContentClasses
            |> List.map (fun contentClass -> contentClass, 1L)

        Assert.That(
            (RepositoryStorageUsage.Create UsageScope.Empty Instant.MinValue all)
                .Coverage,
            Is.EqualTo StorageUsageCoverage.Complete
        )

        Assert.That(
            (RepositoryStorageUsage.Create UsageScope.Empty Instant.MinValue all.Tail)
                .Coverage,
            Is.EqualTo StorageUsageCoverage.Partial
        )
