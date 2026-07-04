namespace Grace.Operations.Tests

open NUnit.Framework

/// Verifies that the operations skeleton projects compile into distinct assemblies.
[<TestFixture>]
type ``Operations skeleton project graph``() =

    /// Proves the core operations assembly is present without introducing public placeholder contracts.
    [<Test>]
    member _.``Core operations assembly marker resolves``() =
        Assert.That(
            typeof<Grace.Operations.OperationsAssembly>
                .Assembly
                .GetName()
                .Name,
            Is.EqualTo("Grace.Operations")
        )

    /// Proves the operations data assembly is present without introducing persistence behavior.
    [<Test>]
    member _.``Operations data assembly marker resolves``() =
        Assert.That(
            typeof<Grace.Operations.Data.OperationsDataAssembly>
                .Assembly
                .GetName()
                .Name,
            Is.EqualTo("Grace.Operations.Data")
        )

    /// Proves the operations worker assembly is present without wiring ingestion hosting.
    [<Test>]
    member _.``Operations worker assembly marker resolves``() =
        Assert.That(
            typeof<Grace.Operations.Worker.OperationsWorkerAssembly>
                .Assembly
                .GetName()
                .Name,
            Is.EqualTo("Grace.Operations.Worker")
        )
