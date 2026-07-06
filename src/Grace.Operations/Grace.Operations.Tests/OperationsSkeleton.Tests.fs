namespace Grace.Operations.Tests

open NUnit.Framework
open System
open System.IO
open System.Xml.Linq

/// Locates project files from the compiled test assembly so layout tests work from any test working directory.
module private ProjectStructureTestPaths =

    /// Finds the repository `src` directory by walking up to the root solution.
    let srcDirectory () =
        let rec find (directory: DirectoryInfo) =
            let solutionPath = Path.Combine(directory.FullName, "Grace.slnx")

            if File.Exists solutionPath then
                directory.FullName
            elif isNull directory.Parent then
                failwith $"Could not find Grace.slnx above {TestContext.CurrentContext.TestDirectory}."
            else
                find directory.Parent

        find (DirectoryInfo(TestContext.CurrentContext.TestDirectory))

    /// Resolves a path relative to the repository `src` directory.
    let srcRelativePath directory fileName = Path.Combine(srcDirectory (), directory, fileName)

/// Reads solution and project files for dependency-boundary assertions.
module private ProjectStructureXml =

    /// Returns project paths listed by a `.slnx` file.
    let solutionProjectPaths (solutionPath: string) =
        XDocument
            .Load(solutionPath)
            .Descendants(XName.Get "Project")
        |> Seq.choose (fun (element: XElement) ->
            match element.Attribute(XName.Get "Path") with
            | null -> None
            | path -> Some path.Value)
        |> Seq.toArray

    /// Returns project reference include paths from an SDK project file.
    let projectReferenceIncludes (projectPath: string) =
        XDocument
            .Load(projectPath)
            .Descendants(XName.Get "ProjectReference")
        |> Seq.choose (fun (element: XElement) ->
            match element.Attribute(XName.Get "Include") with
            | null -> None
            | includePath -> Some includePath.Value)
        |> Seq.toArray

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

    /// Proves the local Operations solution owns the relocated project set.
    [<Test>]
    member _.``Operations solution lists relocated projects under operations root``() =
        let solutionPath = ProjectStructureTestPaths.srcRelativePath "Grace.Operations" "Grace.Operations.slnx"

        let projectPaths = ProjectStructureXml.solutionProjectPaths solutionPath

        let expectedPaths =
            [|
                "Grace.Operations.fsproj"
                "Grace.Operations.Data/Grace.Operations.Data.fsproj"
                "Grace.Operations.Tests/Grace.Operations.Tests.fsproj"
                "Grace.Operations.Worker/Grace.Operations.Worker.fsproj"
            |]

        Assert.That(projectPaths, Is.EquivalentTo(expectedPaths))

    /// Proves the root solution does not reabsorb Operations projects from the local Operations solution boundary.
    [<Test>]
    member _.``Root solution excludes Operations projects``() =
        let solutionPath = ProjectStructureTestPaths.srcRelativePath "" "Grace.slnx"

        let operationsProjectPaths =
            ProjectStructureXml.solutionProjectPaths solutionPath
            |> Array.filter (fun (projectPath: string) -> projectPath.Contains("Grace.Operations", StringComparison.OrdinalIgnoreCase))

        Assert.That(operationsProjectPaths, Is.Empty)

    /// Proves Grace Server does not take a direct dependency on Operations application projects.
    [<Test>]
    member _.``Grace Server project does not reference Operations projects``() =
        let serverProject = ProjectStructureTestPaths.srcRelativePath "Grace.Server" "Grace.Server.fsproj"

        let operationsReferences =
            ProjectStructureXml.projectReferenceIncludes serverProject
            |> Array.filter (fun (includePath: string) -> includePath.Contains("Grace.Operations", StringComparison.OrdinalIgnoreCase))

        Assert.That(operationsReferences, Is.Empty)
