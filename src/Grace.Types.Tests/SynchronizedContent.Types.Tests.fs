namespace Grace.Types.Tests

open Grace.Shared.Parameters.SynchronizedContent
open Grace.Shared.Validation.SynchronizedContent
open Grace.Types.Common
open Grace.Types.Repository
open Grace.Types.SynchronizedContent
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

/// Verifies the settled Synchronized Content wire, validation, and initial repository contract.
[<TestFixture>]
type SynchronizedContentTypesTests() =

    /// Builds metadata with stable values for repository event replay tests.
    let metadata timestamp =
        { Timestamp = timestamp; CorrelationId = "sync-types-test"; Principal = "test-principal"; ClientType = None; Properties = Dictionary<string, string>() }

    /// Creates the minimum valid mutation parameter object for shape tests.
    let mutation mutationKind itemKind =
        let parameters = SubmitSynchronizedMutationParameters()
        parameters.MutationKind <- mutationKind
        parameters.ItemKind <- itemKind
        parameters

    /// Returns true when validation rejected a value with a reason.
    let isError result =
        match result with
        | Error _ -> true
        | Ok _ -> false

    /// Asserts a successful validation result without losing its error type to NUnit inference.
    let assertOk expected result =
        match result with
        | Ok actual -> Assert.That(box actual, Is.EqualTo(box expected))
        | Error error -> Assert.Fail($"Expected success but received: {error}")

    /// Confirms repository creation persists a stable, non-null empty root configuration.
    [<Test>]
    member _.``repository creation persists one stable empty synchronized root configuration``() =
        let repositoryId = Guid.Parse("c9a233f1-a9be-4f18-a862-5d5c27accf0d")
        let timestamp = Instant.FromUtc(2026, 8, 27, 20, 30)

        let created =
            {
                Event =
                    RepositoryEventType.Created(
                        RepositoryName "repository",
                        repositoryId,
                        Guid.Parse("ac90c1d2-95dd-46b8-b9cd-3b52902bc36b"),
                        Guid.Parse("a2a72d17-b44c-4ebf-ae87-fb0019f92d6f"),
                        ObjectStorageProvider.AzureBlobStorage
                    )
                Metadata = metadata timestamp
            }

        let first = RepositoryDto.UpdateDto created RepositoryDto.Default
        let replay = RepositoryDto.UpdateDto created RepositoryDto.Default

        Assert.Multiple(
            Action (fun () ->
                Assert.That(first.SynchronizedRootConfiguration.RepositoryId, Is.EqualTo(repositoryId))
                Assert.That(first.SynchronizedRootConfiguration.Version, Is.Not.EqualTo(Guid.Empty))
                Assert.That(first.SynchronizedRootConfiguration.Roots, Is.Empty)
                Assert.That(first.SynchronizedRootConfiguration.CreatedAt, Is.EqualTo(timestamp))
                Assert.That(first.SynchronizedRootConfiguration.CreatedBy, Is.EqualTo("test-principal"))
                Assert.That(replay.SynchronizedRootConfiguration.Version, Is.EqualTo(first.SynchronizedRootConfiguration.Version)))
        )

    /// Confirms portable normalization accepts slash conversion and NFC while excluding Grace internal state.
    [<Test>]
    member _.``portable path normalization is stable and rejects Grace internal paths``() =
        let decomposed = "shared\\cafe\u0301.txt"

        Assert.Multiple(
            Action (fun () ->
                assertOk "shared/café.txt" (normalizeRepositoryRelativePath decomposed)
                Assert.That(isError (normalizeRepositoryRelativePath ".grace/state"), Is.True)
                Assert.That(isError (normalizeRepositoryRelativePath "shared/CON.txt"), Is.True)
                Assert.That(isError (normalizeRepositoryRelativePath "shared/name."), Is.True)
                Assert.That(isError (normalizeRepositoryRelativePath "../escape"), Is.True))
        )

    /// Confirms normalized roots are sorted and overlap is never silently collapsed.
    [<Test>]
    member _.``root normalization sorts unique roots and rejects overlap``() =
        Assert.Multiple(
            Action (fun () ->
                assertOk [| "Alpha"; "zeta" |] (normalizeRoots [| "zeta"; "Alpha" |])

                Assert.That(
                    isError (
                        normalizeRoots [| "shared"
                                          "shared/assets" |]
                    ),
                    Is.True
                )

                Assert.That(isError (normalizeRoots [| "shared"; "SHARED" |]), Is.True))
        )

    /// Confirms each mutation kind accepts only its specified authority fields.
    [<Test>]
    member _.``mutation validation enforces exact field combinations``() =
        let parent = { Kind = "root"; RootPath = Some "shared"; ItemId = None }
        let slot = { Parent = parent; Name = "file.txt"; ExpectedSlotVersion = Guid.NewGuid(); ExpectedState = "vacant" }

        let createFile = mutation MutationKind.CreateFile ItemKind.File
        createFile.CreationSlotExpectation <- Some slot
        createFile.PreparedContentId <- Nullable(Guid.NewGuid())

        let deleteDirectory = mutation MutationKind.Delete ItemKind.Directory
        deleteDirectory.ItemId <- Nullable(Guid.NewGuid())

        deleteDirectory.NamespacePrecondition <- Some { ItemId = deleteDirectory.ItemId.Value; ExpectedNamespaceVersion = Guid.NewGuid() }

        let invalidRename = mutation MutationKind.Rename ItemKind.File
        invalidRename.ItemId <- Nullable(Guid.NewGuid())

        Assert.Multiple(
            Action (fun () ->
                assertOk () (validateMutationShape createFile)
                assertOk () (validateMutationShape deleteDirectory)
                Assert.That(isError (validateMutationShape invalidRename), Is.True))
        )

    /// Confirms hashes, tokens, and paging obey the public wire bounds.
    [<Test>]
    member _.``wire scalar bounds reject uppercase hashes oversized tokens and pages``() =
        Assert.Multiple(
            Action (fun () ->
                Assert.That(isLowercaseHash (String.replicate 64 "a"), Is.True)
                Assert.That(isLowercaseHash (String.replicate 64 "A"), Is.False)
                Assert.That(opaqueTokenIsValid (String.replicate 2048 "a"), Is.True)
                Assert.That(opaqueTokenIsValid (String.replicate 2049 "a"), Is.False)
                Assert.That(pageSizeIsValid 1, Is.True)
                Assert.That(pageSizeIsValid 2000, Is.True)
                Assert.That(pageSizeIsValid 2001, Is.False))
        )
