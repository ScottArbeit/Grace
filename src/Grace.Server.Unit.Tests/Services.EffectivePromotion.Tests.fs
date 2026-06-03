namespace Grace.Server.Tests

open Grace.Actors
open Grace.Types.Reference
open Grace.Types.Types
open NodaTime
open NUnit.Framework
open System

[<Parallelizable(ParallelScope.All)>]
type ServicesEffectivePromotionTests() =

    let createPromotionReference createdAt isDeleted hasTerminalLink =
        let links: ReferenceLinkType seq =
            if hasTerminalLink then
                [
                    ReferenceLinkType.PromotionSetTerminal(Guid.NewGuid())
                ]
            else
                [
                    ReferenceLinkType.IncludedInPromotionSet(Guid.NewGuid())
                ]

        { ReferenceDto.Default with
            ReferenceId = Guid.NewGuid()
            BranchId = Guid.NewGuid()
            DirectoryId = Guid.NewGuid()
            ReferenceType = ReferenceType.Promotion
            CreatedAt = createdAt
            Links = links
            DeletedAt = if isDeleted then Some(createdAt + Duration.FromMinutes(1.0)) else Option.None
        }

    [<Test>]
    member _.LatestEffectivePromotionIgnoresDeletedTerminalReferences() =
        let createdAt = Instant.FromUtc(2026, 2, 21, 10, 0)

        let deletedLatestTerminal = createPromotionReference (createdAt + Duration.FromMinutes(3.0)) true true
        let latestNonTerminal = createPromotionReference (createdAt + Duration.FromMinutes(2.0)) false false
        let previousTerminal = createPromotionReference (createdAt + Duration.FromMinutes(1.0)) false true

        let selected =
            Services.tryGetLatestEffectivePromotionReference (
                [
                    deletedLatestTerminal
                    latestNonTerminal
                    previousTerminal
                ]
            )

        Assert.That(selected, Is.EqualTo(Some previousTerminal))

    [<Test>]
    member _.LatestEffectivePromotionRequiresTerminalLink() =
        let createdAt = Instant.FromUtc(2026, 2, 21, 12, 0)
        let nonTerminal = createPromotionReference createdAt false false

        let selected = Services.tryGetLatestEffectivePromotionReference ([ nonTerminal ])

        Assert.That(selected, Is.EqualTo(None))

    [<Test>]
    member _.LatestReferenceSelectionIgnoresDeletedReferences() =
        let createdAt = Instant.FromUtc(2026, 2, 21, 14, 0)
        let deletedLatest = createPromotionReference (createdAt + Duration.FromMinutes(2.0)) true true
        let previousActive = createPromotionReference (createdAt + Duration.FromMinutes(1.0)) false false

        let selected = Services.tryGetLatestNotDeletedReference ([ deletedLatest; previousActive ])

        Assert.That(selected, Is.EqualTo(Some previousActive))
