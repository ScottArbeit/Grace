namespace Grace.Server.Tests

open NUnit.Framework
open System
open System.IO

[<Parallelizable(ParallelScope.All)>]
type OrleansPartitionKeyProviderTests() =

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

    [<Test>]
    member _.WorkItemNumberCounterMapsToRepositoryPartitionKey() =
        let filePath = tryResolveSourcePath ()
        let sourceText = File.ReadAllText(filePath)

        Assert.That(sourceText, Does.Contain("| StateName.WorkItemNumberCounter -> repositoryId ()"))
