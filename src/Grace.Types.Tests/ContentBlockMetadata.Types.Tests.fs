namespace Grace.Types.Tests

open Grace.Types.ContentBlockMetadata
open Grace.Types.Common
open Microsoft.FSharp.Reflection
open NodaTime
open NUnit.Framework
open Orleans

[<TestFixture>]
type ContentBlockMetadataTypesTests() =

    let timestamp = Instant.FromUtc(2026, 5, 24, 15, 0)

    let range ordinalStart ordinalCount physicalOffset physicalLength =
        { OrdinalStart = ordinalStart; OrdinalCount = ordinalCount; ActiveManifestCount = 1; PhysicalOffset = physicalOffset; PhysicalLength = physicalLength }

    let metadata ranges =
        {
            Class = nameof ContentBlockMetadata
            StoragePoolId = StoragePoolId "pool-main"
            ContentBlockAddress = ContentBlockAddress "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            BlockFormatVersion = 1s
            StoragePlacement =
                {
                    StorageAccountName = "cas-account"
                    StorageContainerName = StorageContainerName "cas-container"
                    ObjectKey = "cas/content/aa/aa/aa/aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                    ETag = Some "etag-1"
                }
            Ranges = ranges
            TotalPhysicalBytes =
                ranges
                |> Array.sumBy (fun range -> range.PhysicalLength)
            ActivePhysicalBytes =
                ranges
                |> Array.sumBy (fun range -> range.PhysicalLength)
            MetadataVersion = 1L
            UpdatedAt = timestamp
        }

    [<Test>]
    member _.ContentBlockMetadataCommandCasesHaveStableSerializerIds() =
        let actual =
            FSharpType.GetUnionCases(typeof<ContentBlockMetadataCommand>)
            |> Array.map (fun unionCase ->
                let serializerId =
                    unionCase.GetCustomAttributes()
                    |> Seq.choose (function
                        | :? IdAttribute as attribute -> Some attribute
                        | _ -> None)
                    |> Seq.exactlyOne

                unionCase.Name, serializerId.Id)

        Assert.That(
            actual,
            Is.EquivalentTo(
                [|
                    "ReplaceWholeRecord", 0u
                    "MergePhysicalRanges", 1u
                    "CompactPhysicalRanges", 2u
                    "SetCompactionChurnState", 3u
                |]
            )
        )

    [<Test>]
    member _.FindRangeEvidenceBacktracksAcrossAlternateContiguousChains() =
        let lowerOffsetPartial = range 0 4 0L 400L
        let completeFirst = range 0 4 1024L 400L
        let completeSecond = range 4 4 1424L 400L

        let currentMetadata =
            metadata [| lowerOffsetPartial
                        completeFirst
                        completeSecond |]

        let evidence = findRangeEvidence currentMetadata { OrdinalStart = 0; OrdinalCount = 8 }

        Assert.That(evidence, Has.Length.EqualTo(2))
        Assert.That(evidence[0].PhysicalOffset, Is.EqualTo(completeFirst.PhysicalOffset))
        Assert.That(evidence[1].PhysicalOffset, Is.EqualTo(completeSecond.PhysicalOffset))

        let synthesized = findRanges currentMetadata { OrdinalStart = 0; OrdinalCount = 8 }

        Assert.That(synthesized, Has.Length.EqualTo(1))
        Assert.That(synthesized[0].PhysicalOffset, Is.EqualTo(completeFirst.PhysicalOffset))

    [<Test>]
    member _.FindRangesPrefersActiveContiguousEvidenceOverInactiveExactRange() =
        let inactiveExact = { range 0 8 0L 800L with ActiveManifestCount = 0 }

        let activeFirst = range 0 4 1024L 400L
        let activeSecond = range 4 4 1424L 400L

        let currentMetadata =
            metadata [| inactiveExact
                        activeFirst
                        activeSecond |]

        let ranges = findRanges currentMetadata { OrdinalStart = 0; OrdinalCount = 8 }
        let evidence = findRangeEvidence currentMetadata { OrdinalStart = 0; OrdinalCount = 8 }

        Assert.That(ranges, Has.Length.EqualTo(1))
        Assert.That(ranges[0].ActiveManifestCount, Is.EqualTo(1))
        Assert.That(ranges[0].PhysicalOffset, Is.EqualTo(activeFirst.PhysicalOffset))

        Assert.That(evidence, Has.Length.EqualTo(2))

        Assert.That(
            evidence
            |> Array.map (fun range -> range.PhysicalOffset),
            Is.EqualTo<int64>(
                [|
                    activeFirst.PhysicalOffset
                    activeSecond.PhysicalOffset
                |]
            )
        )

    [<Test>]
    member _.FindRangesSlicesActiveCoveringRangeForLaterQueryWindow() =
        let inactiveExact = { range 256 256 8192L 512L with ActiveManifestCount = 0 }
        let activeCoveringRange = range 0 512 0L 1024L

        let currentMetadata =
            metadata [| inactiveExact
                        activeCoveringRange |]

        let query = { OrdinalStart = 256; OrdinalCount = 256 }
        let ranges = findRanges currentMetadata query
        let evidence = findRangeEvidence currentMetadata query

        Assert.That(ranges, Has.Length.EqualTo(1))
        Assert.That(ranges[0].ActiveManifestCount, Is.EqualTo(1))
        Assert.That(ranges[0].OrdinalStart, Is.EqualTo(query.OrdinalStart))
        Assert.That(ranges[0].OrdinalCount, Is.EqualTo(query.OrdinalCount))
        Assert.That(ranges[0].PhysicalOffset, Is.EqualTo(512L))
        Assert.That(ranges[0].PhysicalLength, Is.EqualTo(512L))

        Assert.That(evidence, Has.Length.EqualTo(1))
        Assert.That(evidence[0], Is.EqualTo(ranges[0]))
