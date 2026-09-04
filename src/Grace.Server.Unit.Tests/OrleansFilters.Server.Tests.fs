namespace Grace.Server.Tests

open Grace.Server.LibraryPersistence
open Microsoft.Extensions.Options
open NUnit.Framework
open Orleans.Configuration
open Orleans.Persistence.Cosmos
open Orleans.Runtime
open System
open System.IO

/// Covers orleans Partition Key Provider behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type OrleansPartitionKeyProviderTests() =

    /// Builds deterministic Orleans cluster options for document-identity tests.
    let clusterOptions () =
        let value = ClusterOptions()
        value.ClusterId <- "library-document-key-tests"
        value.ServiceId <- "grace-tests"
        Options.Create(value)

    /// Reads the complete Cosmos key returned synchronously by one Grace document provider.
    let documentKey (provider: IDocumentIdProvider) grainKey =
        provider
            .GetDocumentKey("LibraryRecord", GrainId.Parse($"library-record/{grainKey}"))
            .GetAwaiter()
            .GetResult()

    /// Builds try Resolve Source Path test data for the server unit orleans Filters scenarios in this file.
    let tryResolveSourcePath () =
        let mutable current = DirectoryInfo(Environment.CurrentDirectory)
        let mutable resolvedPath = String.Empty

        while isNull current |> not
              && String.IsNullOrWhiteSpace(resolvedPath) do
            let candidate = Path.Combine(current.FullName, "src", "Grace.Server", "OrleansFilters.Server.fs")

            if File.Exists(candidate) then
                resolvedPath <- candidate
            else
                current <- current.Parent

        if String.IsNullOrWhiteSpace(resolvedPath) then
            failwith "Could not locate src/Grace.Server/OrleansFilters.Server.fs from the current test directory."
        else
            resolvedPath

    /// Verifies that work Item Number Counter Maps To Repository Partition Key.
    [<Test>]
    member _.WorkItemNumberCounterMapsToRepositoryPartitionKey() =
        let filePath = tryResolveSourcePath ()
        let sourceText = File.ReadAllText(filePath)

        Assert.That(sourceText, Does.Contain("| StateName.WorkItemNumberCounter -> repositoryId ()"))

    /// Verifies that content Block Metadata Maps To First Grain Key Segment.
    [<Test>]
    member _.ContentBlockMetadataMapsToFirstGrainKeySegment() =
        let filePath = tryResolveSourcePath ()
        let sourceText = File.ReadAllText(filePath)

        Assert.That(sourceText, Does.Contain("let firstGrainKeySegment () = $\"{grainId.Key}\".Split('|')[0]"))
        Assert.That(sourceText, Does.Contain("| StateName.ContentBlockMetadata -> firstGrainKeySegment ()"))

    /// Verifies that manifest Content Boundary Actors Map To Repository Segment.
    [<Test>]
    member _.ManifestContentBoundaryActorsMapToRepositorySegment() =
        let filePath = tryResolveSourcePath ()
        let sourceText = File.ReadAllText(filePath)

        Assert.That(sourceText, Does.Contain("| StateName.RepositoryContentCounter -> firstGrainKeySegment ()"))
        Assert.That(sourceText, Does.Contain("| StateName.ManifestContributionWorkflow -> firstGrainKeySegment ()"))

    /// Verifies all six Library storage purposes return the ordered partition-key depth configured for their Cosmos containers.
    [<Test>]
    member _.LibraryDocumentProvidersMapAllSixStoragePurposes() =
        let options = clusterOptions ()
        let repositoryId = "8cda01b1-20b8-4912-91c7-d2b38ff69008"

        let cases: (string * IDocumentIdProvider * string * string array) list =
            [
                ControlStorageName, (LibraryControlDocumentIdProvider(options) :> IDocumentIdProvider), repositoryId, [| repositoryId |]
                ChangesStorageName,
                (LibraryTwoLevelDocumentIdProvider(options) :> IDocumentIdProvider),
                $"{repositoryId}|00000000000000000001",
                [|
                    repositoryId
                    "00000000000000000001"
                |]
                CurrentStorageName,
                (LibraryTwoLevelDocumentIdProvider(options) :> IDocumentIdProvider),
                $"{repositoryId}|item|item-7",
                [| repositoryId; "item" |]
                ReceiptsStorageName,
                (LibraryThreeLevelDocumentIdProvider(options) :> IDocumentIdProvider),
                $"{repositoryId}|operation|op-9",
                [| repositoryId; "operation"; "op-9" |]
                HistoryStorageName,
                (LibraryThreeLevelDocumentIdProvider(options) :> IDocumentIdProvider),
                $"{repositoryId}|item-7|00000000000000000002",
                [|
                    repositoryId
                    "item-7"
                    "00000000000000000002"
                |]
                BaselinesStorageName,
                (LibraryThreeLevelDocumentIdProvider(options) :> IDocumentIdProvider),
                $"{repositoryId}|baseline-3|shard-4",
                [|
                    repositoryId
                    "baseline-3"
                    "shard-4"
                |]
            ]

        Assert.Multiple(
            Action (fun () ->
                for storageName, provider, grainKey, expectedValues in cases do
                    let actual =
                        (documentKey provider grainKey).PartitionKeyValues
                        |> Seq.toArray

                    Assert.That(actual :> obj, Is.EqualTo(expectedValues :> obj), storageName))
        )

    /// Verifies malformed bounded actor identities fail before Orleans can issue a Cosmos point operation.
    [<Test>]
    member _.LibraryDocumentProviderRejectsMissingPartitionKeyComponents() =
        let provider = LibraryThreeLevelDocumentIdProvider(clusterOptions ()) :> IDocumentIdProvider

        let error =
            Assert.Throws<ArgumentException>(
                Action (fun () ->
                    documentKey provider "repository-only|receipt"
                    |> ignore)
            )

        Assert.That(error.Message, Does.Contain("requires 3 non-empty ordered key component"))

    /// Verifies provider-neutral Orleans purpose names do not couple non-Cosmos storage registration to Cosmos container names.
    [<Test>]
    member _.LibraryStoragePurposeNamesRemainProviderNeutral() =
        let storageNames =
            [|
                ControlStorageName
                ChangesStorageName
                CurrentStorageName
                ReceiptsStorageName
                HistoryStorageName
                BaselinesStorageName
            |]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(storageNames, Has.Length.EqualTo(6))
                Assert.That(storageNames, Is.Unique)

                storageNames
                |> Array.iter (fun storageName -> Assert.That(storageName, Does.Not.Contain("cosmos"))))
        )
