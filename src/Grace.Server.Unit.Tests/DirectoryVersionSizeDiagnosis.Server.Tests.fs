namespace Grace.Server.Unit.Tests

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open System.Text.Json.Nodes
open Grace.Actors.Services
open Grace.Server.DirectoryVersionSizeDiagnosis
open Grace.Shared
open Grace.Shared.Parameters.Repository
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Grace.Types.Usage
open NUnit.Framework

/// Exercises bounded declaration aggregation independently of Cosmos and HTTP hosting.
[<Parallelizable(ParallelScope.All)>]
type DirectoryVersionSizeDiagnosisTests() =
    let scope = { OwnerId = Guid.NewGuid(); OrganizationId = Guid.NewGuid(); RepositoryId = Guid.NewGuid() }

    /// Creates real file declarations with stable storage identities.
    let file path size = FileVersion.CreateWithHashes path (String.replicate 64 "a") (String.replicate 64 "b") "" true size

    /// Uses Grace's event serializer and fold input, retaining direct files through logical deletion.
    let row (files: FileVersion seq) =
        let directory = DirectoryVersion()
        directory.DirectoryVersionId <- Guid.NewGuid()
        directory.OwnerId <- scope.OwnerId
        directory.OrganizationId <- scope.OrganizationId
        directory.RepositoryId <- scope.RepositoryId
        directory.CreatedAt <- getCurrentInstant ()
        directory.Files.AddRange files

        let metadata =
            {
                Timestamp = getCurrentInstant ()
                CorrelationId = "diagnostic-test"
                Principal = "test"
                ClientType = None
                Properties = Dictionary<string, string>()
            }

        let events =
            [|
                { Event = Created directory; Metadata = metadata }
                { Event = RecursiveSizeSet 999L; Metadata = metadata }
                { Event = LogicalDeleted "retained"; Metadata = metadata }
            |]

        let result = DirectoryVersionEventValue()
        result.State <- deserialize<DirectoryVersionEvent array> (serialize events)
        result

    /// Delivers finite pages through the production aggregation seam.
    let enumerate (pages: DirectoryVersionEventValue array array) =
        let mutable index = 0

        enumerateWith
            (fun _ ->
                let page = pages[index]
                index <- index + 1
                Task.FromResult(page, index < pages.Length))
            scope
            "diagnostic-test"
            CancellationToken.None

    /// Verifies failure cannot escape as a successful diagnostic containing a quantity.
    let expectFailure (operation: unit -> Task<DirectoryVersionSizeDiagnostic>) =
        task {
            let! result =
                task {
                    try
                        let! value = operation ()
                        return Choice1Of2 value
                    with
                    | error -> return Choice2Of2 error
                }

            match result with
            | Choice1Of2 value -> Assert.Fail($"Unexpected success: {value.DeclaredLogicalBytes} bytes")
            | Choice2Of2 _ -> ()
        }

    /// Counts repeated whole-file and manifest identities once while preserving distinct storage keys and logical deletion.
    [<Test>]
    member _.``deduplicates storage identities and retains logically deleted declarations``() =
        task {
            let manifest =
                FileManifest.Create(
                    "",
                    "suite",
                    String.replicate 64 "b",
                    20L,
                    "pool",
                    [
                        ContentBlock.Create(String.replicate 64 "c", 0L, 20L)
                    ]
                )

            let manifest = { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }
            let manifestFile = file "manifest.txt" 20L
            manifestFile.ContentReference <- FileContentReference.FileManifest manifest
            let first = row [ file "a.txt" 10L; manifestFile ]

            let second =
                row [ file "a.txt" 10L
                      file "b.txt" 10L
                      manifestFile ]

            let! result =
                enumerate [| [| first |]
                             [| second |] |]

            Assert.That(result.DeclaredLogicalBytes, Is.EqualTo 40L)
            Assert.That(result.DistinctContentCount, Is.EqualTo 3L)
            Assert.That(result.Scope, Is.EqualTo scope)
            Assert.That(result.EnumerationFinishedAt, Is.GreaterThanOrEqualTo result.EnumerationStartedAt)
        }

    /// Distinguishes no declarations from a declaration of zero bytes.
    [<Test>]
    member _.``exhausted empty and zero length content both report known zero``() =
        task {
            let! empty = enumerate [| [||] |]
            let! zero = enumerate [| [| row [ file "empty.txt" 0L ] |] |]
            Assert.That(empty.DeclaredLogicalBytes, Is.Zero)
            Assert.That(empty.DistinctContentCount, Is.Zero)
            Assert.That(zero.DeclaredLogicalBytes, Is.Zero)
            Assert.That(zero.DistinctContentCount, Is.EqualTo 1L)
        }

    /// Rejects declarations that cannot support the selected scoped byte meaning.
    [<TestCase("conflict")>]
    [<TestCase("negative")>]
    [<TestCase("overflow")>]
    [<TestCase("scope")>]
    [<TestCase("created")>]
    [<TestCase("reference")>]
    [<TestCase("manifest-size")>]
    member _.``invalid declarations yield no diagnostic``(kind: string) =
        task {
            let candidate = row [ file "a.txt" 10L ]

            let directory =
                (match candidate.State[0].Event with
                 | Created value -> value
                 | _ -> failwith "fixture")

            match kind with
            | "conflict" -> directory.Files.Add(file "a.txt" 11L)
            | "negative" -> directory.Files.Add(file "negative" -1L)
            | "overflow" -> directory.Files.Add(file "large" Int64.MaxValue)
            | "scope" -> directory.OwnerId <- Guid.NewGuid()
            | "created" -> candidate.State <- candidate.State[1..]
            | "reference" ->
                directory.Files[0].ContentReference <- { FileContentReference.WholeFileContent with ReferenceType = FileContentReferenceType.FileManifest }
            | "manifest-size" ->
                directory.Files[0].ContentReference <- FileContentReference.FileManifest(
                    FileManifest.Create(
                        String.replicate 64 "a",
                        "suite",
                        String.replicate 64 "b",
                        20L,
                        "pool",
                        [
                            ContentBlock.Create(String.replicate 64 "c", 0L, 20L)
                        ]
                    )
                )
            | _ -> failwith "fixture"

            do! expectFailure (fun () -> enumerate [| [| candidate |] |])
        }

    /// Caps pages, documents, and direct references without returning a truncated quantity.
    [<TestCase("pages")>]
    [<TestCase("documents")>]
    [<TestCase("references")>]
    member _.``work bounds fail without partial success``(kind: string) =
        task {
            let pages =
                match kind with
                | "pages" -> Array.init 33 (fun _ -> [||])
                | "documents" -> [| Array.init 10001 (fun _ -> row []) |]
                | _ ->
                    [|
                        [|
                            row (Seq.init 100001 (fun _ -> file "same" 0L))
                        |]
                    |]

            do! expectFailure (fun () -> enumerate pages)
        }

    /// Allows exhaustion exactly at the page boundary.
    [<Test>]
    member _.``last allowed page may exhaust successfully``() =
        task {
            let! result = enumerate (Array.init 32 (fun _ -> [||]))
            Assert.That(result.DeclaredLogicalBytes, Is.Zero)
        }

    /// Rejects dependency failure and cancellation after an already observed page.
    [<TestCase(false)>]
    [<TestCase(true)>]
    member _.``partial read failure or cancellation yields no diagnostic``(cancel: bool) =
        task {
            use token = new CancellationTokenSource()
            let mutable reads = 0

            /// Fails the second page after the first page has contributed a valid declaration.
            let read _ =
                task {
                    reads <- reads + 1

                    if reads = 1 then
                        return [| row [ file "a" 10L ] |], true
                    else
                        if cancel then
                            token.Cancel()
                            token.Token.ThrowIfCancellationRequested()

                        return raise (IOException "provider failure")
                }

            do! expectFailure (fun () -> enumerateWith read scope "diagnostic-test" token.Token)
            Assert.That(reads, Is.EqualTo 2)
        }

    /// A complete mixed-page observation carries its read window without claiming a snapshot.
    [<Test>]
    member _.``mixed pages report observed declarations only``() =
        task {
            let! result =
                enumerate [| [| row [ file "a" 10L ] |]
                             [| row [ file "b" 40L ] |] |]

            Assert.That(result.DeclaredLogicalBytes, Is.EqualTo 50L)
            Assert.That(result.DeclaredLogicalBytes, Is.Not.EqualTo 30L)
            Assert.That(result.DeclaredLogicalBytes, Is.Not.EqualTo 70L)
        }

    /// Prevents absent persisted fields from being mistaken for valid empty declarations during deserialization.
    [<TestCase("Files")>]
    [<TestCase("CreatedAt")>]
    [<TestCase("ContentReference")>]
    member _.``missing wire fields cannot become constructor defaults``(field: string) =
        let candidate = row [ file "a.txt" 0L ]
        let node = JsonNode.Parse(serialize candidate)
        let state = node["State"]
        let first = state[0]
        let event = first["Event"]
        let created = event["created"]
        let files = created["Files"]
        let target = if field = "Files" || field = "CreatedAt" then created else files[0]
        Assert.That(target.AsObject().Remove field, Is.True)
        use document = JsonDocument.Parse(node.ToJsonString())
        let error = Assert.Throws<InvalidDataException>(Action(fun () -> decodeDocument document.RootElement |> ignore))
        Assert.That(error.Message, Does.Contain field)

    /// Honors Grace's real zero encoding without accepting null, fractional, or malformed declared lengths.
    [<Test>]
    member _.``serialized omitted size is zero while malformed explicit sizes fail``() =
        task {
            let candidate = row [ file "empty.txt" 0L ]
            let wire = serialize candidate
            use document = JsonDocument.Parse wire
            let state = document.RootElement.GetProperty "State"

            let files =
                state[0]
                    .GetProperty("Event")
                    .GetProperty("created")
                    .GetProperty("Files")

            let fileElement = files[0]
            let mutable size = Unchecked.defaultof<JsonElement>
            Assert.That(fileElement.TryGetProperty("Size", &size), Is.False)

            let! result =
                enumerate [| [|
                                 decodeDocument document.RootElement
                             |] |]

            Assert.That(result.DeclaredLogicalBytes, Is.Zero)
            Assert.That(result.DistinctContentCount, Is.EqualTo 1L)

            [
                "null"
                "\"invalid\""
                "1.5"
                "9223372036854775808"
            ]
            |> List.iter (fun invalid ->
                let node = JsonNode.Parse wire
                let state = node["State"]
                let first = state[0]
                let event = first["Event"]
                let created = event["created"]
                let files = created["Files"]
                let file = files[0]
                file["Size"] <- JsonNode.Parse invalid
                use malformed = JsonDocument.Parse(node.ToJsonString())
                let error = Assert.Throws<InvalidDataException>(Action(fun () -> decodeDocument malformed.RootElement |> ignore))
                Assert.That(error.Message, Does.Contain "Size"))
        }

    /// Keeps malformed persisted values distinct from malformed request-body errors.
    [<Test>]
    member _.``invalid persisted GUID is a source decoding failure``() =
        let node = JsonNode.Parse(serialize (row []))
        let state = node["State"]
        let first = state[0]
        let event = first["Event"]
        let created = event["created"]
        created["OwnerId"] <- JsonValue.Create("invalid")
        use document = JsonDocument.Parse(node.ToJsonString())
        let error = Assert.Throws<InvalidDataException>(Action(fun () -> decodeDocument document.RootElement |> ignore))
        Assert.That(error.Message, Does.Contain "source could not be decoded")

    /// Accepts explicit IDs and rejects missing IDs or names before a source call can occur.
    [<Test>]
    member _.``scope parameters require all IDs and no names``() =
        let parameters =
            GetRepositoryParameters(OwnerId = string scope.OwnerId, OrganizationId = string scope.OrganizationId, RepositoryId = string scope.RepositoryId)

        Assert.That(validateParameters parameters, Is.EqualTo(Ok scope: Result<UsageFactScope, string>))
        parameters.RepositoryName <- "ignored-name"
        Assert.That(validateParameters parameters |> Result.isError, Is.True)
        parameters.RepositoryName <- ""
        parameters.OwnerId <- string Guid.Empty
        Assert.That(validateParameters parameters |> Result.isError, Is.True)
