namespace Grace.CLI.Tests

open Grace.CLI.Command
open Grace.Shared.Parameters.Repository
open Grace.Types.Usage
open NodaTime
open NUnit.Framework
open System

/// Covers `grace repository usage` output and the SDK's refusal of name selectors.
[<Parallelizable(ParallelScope.All)>]
type StorageUsageCliTests() =

    /// A partial answer prints every class, says which are not counted yet, and says it is not a total.
    [<Test>]
    member _.``usage table lists every class and states partial coverage``() =
        let usage =
            RepositoryStorageUsage.Create
                UsageScope.Empty
                (Instant.FromUnixTimeSeconds 0L)
                [
                    StorageContentClass.ManifestBackedFiles, 1234567L
                ]

        let rows, coverage = Repository.storageUsageLines usage

        Assert.That(
            rows,
            Is.EqualTo [ "Manifest-backed files", "1,234,567"
                         "Whole files", "not counted yet"
                         "Work item text", "not counted yet"
                         "Artifacts", "not counted yet" ]
        )

        Assert.That(coverage, Is.EqualTo "Coverage: partial (1 of 4 content classes counted). This is not a total.")

    /// Zero is a reading, printed as a number.
    [<Test>]
    member _.``zero bytes print as a number``() =
        let usage =
            RepositoryStorageUsage.Create
                UsageScope.Empty
                (Instant.FromUnixTimeSeconds 0L)
                [
                    StorageContentClass.ManifestBackedFiles, 0L
                ]

        let rows, _ = Repository.storageUsageLines usage
        Assert.That(snd rows.Head, Is.EqualTo "0")

    /// The SDK rejects names and missing IDs before calling the server, rather than sending selectors it would ignore.
    [<Test>]
    member _.``sdk rejects name selectors and missing ids without calling the server``() =
        task {
            let ids () =
                GetRepositoryParameters(
                    OwnerId = string (Guid.NewGuid()),
                    OrganizationId = string (Guid.NewGuid()),
                    RepositoryId = string (Guid.NewGuid()),
                    CorrelationId = "usage-sdk-test"
                )

            let named = ids ()
            named.RepositoryName <- "named"
            let missing = ids ()
            missing.OrganizationId <- ""

            for parameters in [ named; missing ] do
                match! Grace.SDK.Usage.GetStorage(parameters) with
                | Error error -> Assert.That(error.Error, Does.Contain "need explicit owner, organization, and repository GUIDs")
                | Ok _ -> Assert.Fail "Expected the SDK to reject the request locally."
        }
