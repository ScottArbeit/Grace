namespace Grace.Server.Tests

open Grace.Actors
open Grace.Types.Common
open NUnit.Framework
open System

type HashCandidate =
    {
        RepositoryId: RepositoryId
        ReferenceId: ReferenceId
        DirectoryVersionId: DirectoryVersionId
        Sha256Hash: Sha256Hash
        Blake3Hash: Blake3Hash
    }

[<Parallelizable(ParallelScope.All)>]
type ServicesVersionHashPrefixResolutionTests() =
    let repositoryId = Guid.Parse("11111111-3530-4444-8888-111111111111")
    let otherRepositoryId = Guid.Parse("22222222-3530-4444-8888-222222222222")
    let sharedDirectoryVersionId = Guid.Parse("33333333-3530-4444-8888-333333333333")

    let candidate index repositoryId directoryVersionId (sha256Hash: string) (blake3Hash: string) =
        {
            RepositoryId = repositoryId
            ReferenceId = Guid.Parse($"44444444-3530-4444-8888-00000000000{index}")
            DirectoryVersionId = directoryVersionId
            Sha256Hash = Sha256Hash sha256Hash
            Blake3Hash = Blake3Hash blake3Hash
        }

    let uniqueSha256 = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
    let ambiguousSha256 = "abffff0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
    let unrelatedSha256 = "cdffff0123456789abcdef0123456789abcdef0123456789abcdef0123456789"

    [<Test>]
    member _.Sha256PrefixResolutionReturnsNoMatchesForZeroScopedCandidates() =
        let resolution =
            [
                candidate 1 repositoryId (Guid.NewGuid()) unrelatedSha256 "ab-only-in-blake3"
            ]
            |> Services.resolveScopedVersionHashPrefix "ab" (fun candidate -> candidate.Sha256Hash)

        match resolution with
        | Services.NoMatches -> Assert.Pass()
        | _ -> Assert.Fail($"Expected zero matches, got {resolution}.")

    [<Test>]
    member _.Sha256PrefixResolutionReturnsUniqueMatchForOneScopedCandidate() =
        let expected = candidate 1 repositoryId (Guid.NewGuid()) uniqueSha256 "blake3-value"

        let resolution =
            [
                expected
                candidate 2 repositoryId (Guid.NewGuid()) unrelatedSha256 "ab-only-in-blake3"
            ]
            |> Services.resolveScopedVersionHashPrefix "abc" (fun candidate -> candidate.Sha256Hash)

        match resolution with
        | Services.UniqueMatch actual -> Assert.That(actual.ReferenceId, Is.EqualTo(expected.ReferenceId))
        | _ -> Assert.Fail($"Expected one match, got {resolution}.")

    [<Test>]
    member _.Sha256PrefixResolutionReturnsAmbiguousMatchesInsteadOfFirstMatch() =
        let first = candidate 1 repositoryId (Guid.NewGuid()) uniqueSha256 "first-blake3"
        let second = candidate 2 repositoryId (Guid.NewGuid()) ambiguousSha256 "second-blake3"

        let resolution =
            [
                first
                second
                candidate 3 repositoryId (Guid.NewGuid()) "ab11110123456789abcdef0123456789abcdef0123456789abcdef0123456789" "third-blake3"
            ]
            |> Services.resolveScopedVersionHashPrefix "ab" (fun candidate -> candidate.Sha256Hash)

        match resolution with
        | Services.AmbiguousMatches matches ->
            Assert.That(matches, Has.Length.EqualTo(Services.maxVersionHashPrefixResolutionMatches))

            Assert.That(
                matches
                |> Array.map (fun candidate -> candidate.ReferenceId),
                Does.Contain(first.ReferenceId)
            )

            Assert.That(
                matches
                |> Array.map (fun candidate -> candidate.ReferenceId),
                Does.Contain(second.ReferenceId)
            )
        | _ -> Assert.Fail($"Expected ambiguous matches, got {resolution}.")

    [<Test>]
    member _.OptionCompatibilityWrapperOnlyReturnsUniqueMatches() =
        let first = candidate 1 repositoryId (Guid.NewGuid()) uniqueSha256 "first-blake3"
        let second = candidate 2 repositoryId (Guid.NewGuid()) ambiguousSha256 "second-blake3"

        let uniqueResult = Services.tryGetUniqueVersionHashPrefixMatch (Services.UniqueMatch first)

        let zeroResult = Services.tryGetUniqueVersionHashPrefixMatch Services.NoMatches

        let ambiguousResult =
            Services.tryGetUniqueVersionHashPrefixMatch (
                Services.AmbiguousMatches [| first
                                             second |]
            )

        Assert.That(uniqueResult, Is.EqualTo(Some first))
        Assert.That(zeroResult, Is.EqualTo(None))
        Assert.That(ambiguousResult, Is.EqualTo(None))

    [<Test>]
    member _.ExactSha256ResolutionStillRejectsMultipleVersionGraphObjects() =
        let first = candidate 1 repositoryId (Guid.NewGuid()) uniqueSha256 "first-blake3"
        let second = candidate 2 repositoryId (Guid.NewGuid()) uniqueSha256 "second-blake3"

        let resolution =
            [ first; second ]
            |> Services.resolveScopedVersionHashPrefix uniqueSha256 (fun candidate -> candidate.Sha256Hash)

        match resolution with
        | Services.AmbiguousMatches matches -> Assert.That(matches, Has.Length.EqualTo(2))
        | _ -> Assert.Fail($"Expected exact-hash ambiguity, got {resolution}.")

    [<Test>]
    member _.TwoCharacterSha256PrefixUsesSameUniqueRuleWithinRepositoryScope() =
        let expected = candidate 1 repositoryId (Guid.NewGuid()) uniqueSha256 "first-blake3"
        let samePrefixOtherRepository = candidate 2 otherRepositoryId (Guid.NewGuid()) ambiguousSha256 "other-repository-blake3"

        let scopedCandidates =
            [ expected; samePrefixOtherRepository ]
            |> List.filter (fun candidate -> candidate.RepositoryId = repositoryId)

        let resolution =
            scopedCandidates
            |> Services.resolveScopedVersionHashPrefix "ab" (fun candidate -> candidate.Sha256Hash)

        match resolution with
        | Services.UniqueMatch actual -> Assert.That(actual.ReferenceId, Is.EqualTo(expected.ReferenceId))
        | _ -> Assert.Fail($"Expected repository-scoped unique match, got {resolution}.")

    [<Test>]
    member _.ReferenceScopeTreatsMultipleReferencesToOneRootAsAmbiguousReferences() =
        let first = candidate 1 repositoryId sharedDirectoryVersionId uniqueSha256 "first-blake3"
        let second = candidate 2 repositoryId sharedDirectoryVersionId ambiguousSha256 "second-blake3"

        let resolution =
            [ first; second ]
            |> Services.resolveScopedVersionHashPrefix "ab" (fun candidate -> candidate.Sha256Hash)

        match resolution with
        | Services.AmbiguousMatches matches ->
            Assert.That(matches, Has.Length.EqualTo(2))

            Assert.That(
                matches
                |> Array.map (fun candidate -> candidate.DirectoryVersionId)
                |> Array.distinct,
                Has.Length.EqualTo(1)
            )
        | _ -> Assert.Fail($"Expected reference-scope ambiguity, got {resolution}.")

    [<Test>]
    member _.Sha256ResolutionDoesNotMixBlake3NamespaceMatches() =
        let shaOnly = candidate 1 repositoryId (Guid.NewGuid()) uniqueSha256 "cd-blake3"
        let blake3Only = candidate 2 repositoryId (Guid.NewGuid()) unrelatedSha256 "ab-blake3"

        let resolution =
            [ shaOnly; blake3Only ]
            |> Services.resolveScopedVersionHashPrefix "ab" (fun candidate -> candidate.Sha256Hash)

        match resolution with
        | Services.UniqueMatch actual -> Assert.That(actual.ReferenceId, Is.EqualTo(shaOnly.ReferenceId))
        | _ -> Assert.Fail($"Expected SHA-256-only match, got {resolution}.")
