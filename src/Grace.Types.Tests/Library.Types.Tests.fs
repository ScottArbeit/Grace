namespace Grace.Types.Tests

open Grace.Shared.Parameters.Library
open Grace.Shared.Validation.Library
open Grace.Types.Common
open Grace.Types.Library
open NodaTime
open NUnit.Framework
open System

/// Verifies the settled Library Content wire, validation, and initial repository contract.
[<TestFixture>]
type LibraryTypesTests() =

    /// Creates the minimum valid change parameter object for shape tests.
    let change changeKind itemKind =
        let parameters = SubmitLibraryChangeParameters()
        parameters.ChangeKind <- changeKind
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

    /// Confirms repository creation facts produce a stable, non-null empty Library catalog.
    [<Test>]
    member _.``repository creation facts produce one stable empty Library catalog``() =
        let repositoryId = Guid.Parse("c9a233f1-a9be-4f18-a862-5d5c27accf0d")
        let timestamp = Instant.FromUtc(2026, 8, 27, 20, 30)
        let first = LibraryCatalogDto.CreateInitial(repositoryId, timestamp, "test-principal")
        let replay = LibraryCatalogDto.CreateInitial(repositoryId, timestamp, "test-principal")

        Assert.Multiple(
            Action (fun () ->
                Assert.That(first.RepositoryId, Is.EqualTo(repositoryId))
                Assert.That(first.Version, Is.Not.EqualTo(Guid.Empty))
                Assert.That(first.Libraries, Is.Empty)
                Assert.That(first.CreatedAt, Is.EqualTo(timestamp))
                Assert.That(first.CreatedBy, Is.EqualTo("test-principal"))
                Assert.That(replay.Version, Is.EqualTo(first.Version)))
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

    /// Confirms normalized libraries are sorted and overlap is never silently collapsed.
    [<Test>]
    member _.``root normalization sorts unique libraries and rejects overlap``() =
        Assert.Multiple(
            Action (fun () ->
                assertOk [| "Alpha"; "zeta" |] (normalizeLibraries [| "zeta"; "Alpha" |])

                Assert.That(
                    isError (
                        normalizeLibraries [| "shared"
                                              "shared/assets" |]
                    ),
                    Is.True
                )

                Assert.That(
                    isError (
                        normalizeLibraries [| "shared"
                                              "SHARED" |]
                    ),
                    Is.True
                ))
        )

    /// Confirms root changes use exact versions and never normalize overlap into success.
    [<Test>]
    member _.``root transitions require exact version and retain prior configuration on rejection``() =
        let current = LibraryCatalogDto.CreateInitial(Guid.Parse("a345ebd6-037a-45b8-b97e-64d6840bd440"), Instant.FromUtc(2026, 8, 27, 21, 0), "admin")

        let nextVersion = Guid.Parse("90af4bba-a884-46d8-a497-f68ce0b795ed")
        let added = addLibrary current.Version nextVersion "Shared" (Instant.FromUtc(2026, 8, 27, 21, 1)) "admin" current

        match added with
        | Error reason -> Assert.Fail($"Expected root add success but received {reason}.")
        | Ok configured ->
            Assert.Multiple(
                Action (fun () ->
                    Assert.That(box configured.Libraries, Is.EqualTo(box [| "Shared" |]))
                    Assert.That(configured.PreviousVersion, Is.EqualTo(Some current.Version))

                    Assert.That(
                        addLibrary current.Version (Guid.NewGuid()) "Shared/child" (Instant.FromUtc(2026, 8, 27, 21, 2)) "admin" configured
                        |> isError,
                        Is.True
                    )

                    Assert.That(
                        removeLibrary current.Version (Guid.NewGuid()) "Shared" (Instant.FromUtc(2026, 8, 27, 21, 3)) "admin" configured
                        |> isError,
                        Is.True
                    ))
            )

    /// Confirms each change kind accepts only its specified authority fields.
    [<Test>]
    member _.``change validation enforces exact field combinations``() =
        let parent = { Kind = "root"; LibraryPath = Some "shared"; ItemId = None }
        let slot = { Parent = parent; Name = "file.txt"; ExpectedSlotVersion = Guid.NewGuid(); ExpectedState = "vacant" }

        let createFile = change ChangeKind.CreateFile ItemKind.File
        createFile.CreationSlotExpectation <- Some slot
        createFile.PreparedContentId <- Nullable(Guid.NewGuid())

        let deleteDirectory = change ChangeKind.Delete ItemKind.Directory
        deleteDirectory.ItemId <- Nullable(Guid.NewGuid())

        deleteDirectory.NamespacePrecondition <- Some { ItemId = deleteDirectory.ItemId.Value; ExpectedNamespaceVersion = Guid.NewGuid() }

        let invalidRename = change ChangeKind.Rename ItemKind.File
        invalidRename.ItemId <- Nullable(Guid.NewGuid())

        Assert.Multiple(
            Action (fun () ->
                assertOk () (validateChangeShape createFile)
                assertOk () (validateChangeShape deleteDirectory)
                Assert.That(isError (validateChangeShape invalidRename), Is.True))
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
