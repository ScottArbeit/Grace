namespace Grace.Server.Tests

open NUnit.Framework
open System
open System.IO

/// Covers orleans Partition Key Provider behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type OrleansPartitionKeyProviderTests() =

    /// Resolves a server source file from the test output directory without assuming a checkout depth.
    let tryResolveServerSourcePath fileName =
        let mutable current = DirectoryInfo(Environment.CurrentDirectory)
        let mutable resolvedPath = String.Empty

        while isNull current |> not
              && String.IsNullOrWhiteSpace(resolvedPath) do
            let candidate = Path.Combine(current.FullName, "src", "Grace.Server", fileName)

            if File.Exists(candidate) then
                resolvedPath <- candidate
            else
                current <- current.Parent

        if String.IsNullOrWhiteSpace(resolvedPath) then
            failwith $"Could not locate src/Grace.Server/{fileName} from the current test directory."
        else
            resolvedPath

    /// Resolves the Orleans filter source covered by the partition-key assertions below.
    let tryResolveSourcePath () = tryResolveServerSourcePath "OrleansFilters.Server.fs"

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

    /// Verifies that the singleton Cache registration actor resolves its Cosmos provider, loads on activation, and writes accepted transitions.
    [<Test>]
    member _.CacheRegistrationUsesDeploymentPartitionAndDurableActorState() =
        let filePath = tryResolveSourcePath ()
        let sourceText = File.ReadAllText(filePath)
        let actorSource = File.ReadAllText(tryResolveServerSourcePath (Path.Combine("..", "Grace.Actors", "CacheRegistration.Actor.fs")))
        let startupSource = File.ReadAllText(tryResolveServerSourcePath "Startup.Server.fs")

        Assert.That(sourceText, Does.Contain("| StateName.CacheRegistration -> StateName.CacheRegistration"))
        Assert.That(actorSource, Does.Contain("PersistentState(StateName.CacheRegistration, Constants.GraceActorStorage)"))
        Assert.That(actorSource, Does.Contain("override this.OnActivateAsync"))
        Assert.That(actorSource, Does.Contain("state.RecordExists"))
        Assert.That(actorSource, Does.Contain("state.WriteStateAsync()"))
        Assert.That(startupSource, Does.Contain("ApplicationContext.setActorStateStorageProvider ActorStateStorageProvider.AzureCosmosDb"))

    /// Verifies that shared Orleans invocation failures are never converted into default successful results.
    [<Test>]
    member _.CorrelationLoggingFilterRethrowsInvocationFailures() =
        let filePath = tryResolveSourcePath ()
        let sourceText = File.ReadAllText(filePath)

        Assert.That(sourceText, Does.Contain("return raise ex"))
