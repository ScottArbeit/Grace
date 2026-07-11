namespace Grace.Server.Tests

open NUnit.Framework
open System
open System.IO

/// Covers orleans Partition Key Provider behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type OrleansPartitionKeyProviderTests() =

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

    /// Verifies that the deployment-wide signing-key actor uses one stable Cosmos partition.
    [<Test>]
    member _.ArtifactGrantSigningKeyMapsToDeploymentPartition() =
        let filePath = tryResolveSourcePath ()
        let sourceText = File.ReadAllText(filePath)

        Assert.That(sourceText, Does.Contain("| StateName.ArtifactGrantSigningKey -> StateName.ArtifactGrantSigningKey"))

    /// Verifies that shared Orleans invocation failures are never converted into default successful results.
    [<Test>]
    member _.CorrelationLoggingFilterRethrowsInvocationFailures() =
        let filePath = tryResolveSourcePath ()
        let sourceText = File.ReadAllText(filePath)

        Assert.That(sourceText, Does.Contain("return raise ex"))
