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

/// Walks solution project references so root-solution isolation includes transitive dependencies.
module private ProjectStructureGraph =

    /// Describes a project-reference edge discovered while walking a solution's project graph.
    type ProjectDependency = { ReferencingProject: string; ReferencedProject: string; ReferencePath: string }

    /// Converts an absolute project path into a stable repository `src`-relative path.
    let srcRelativeProjectPath (projectPath: string) =
        Path
            .GetRelativePath(ProjectStructureTestPaths.srcDirectory (), projectPath)
            .Replace(Path.DirectorySeparatorChar, '/')

    /// Resolves a solution project entry relative to the repository `src` directory.
    let solutionProjectPath (projectPath: string) = Path.GetFullPath(Path.Combine(ProjectStructureTestPaths.srcDirectory (), projectPath))

    /// Normalizes MSBuild project-reference separators before platform-specific path resolution.
    let normalizeProjectReferencePath (referencePath: string) =
        referencePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)

    /// Resolves a project reference relative to the project file that declares it.
    let projectReferencePath (projectPath: string) (referencePath: string) =
        let projectDirectory =
            match Path.GetDirectoryName projectPath with
            | null -> failwith $"Project path has no directory: {projectPath}"
            | directory -> directory

        Path.GetFullPath(Path.Combine(projectDirectory, normalizeProjectReferencePath referencePath))

    /// Returns every project-reference edge reachable from the supplied root solution projects.
    let projectReferenceClosure (rootProjects: string array) =
        let visited = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let dependencies = Collections.Generic.List<ProjectDependency>()

        let rec visit projectPath =
            let projectPath = Path.GetFullPath projectPath

            if visited.Add projectPath then
                if not (File.Exists projectPath) then
                    failwith $"Project reference does not exist: {projectPath}"

                ProjectStructureXml.projectReferenceIncludes projectPath
                |> Array.iter (fun referencePath ->
                    let referencedProjectPath = projectReferencePath projectPath referencePath

                    dependencies.Add(
                        {
                            ReferencingProject = srcRelativeProjectPath projectPath
                            ReferencedProject = srcRelativeProjectPath referencedProjectPath
                            ReferencePath = referencePath
                        }
                    )

                    visit referencedProjectPath)

        rootProjects |> Array.iter visit
        dependencies |> Seq.toArray

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

    /// Proves root validation cannot pull Operations projects through transitive project references.
    [<Test>]
    member _.``Root solution project graph excludes Operations project references``() =
        let solutionPath = ProjectStructureTestPaths.srcRelativePath "" "Grace.slnx"

        let rootProjects =
            ProjectStructureXml.solutionProjectPaths solutionPath
            |> Array.map ProjectStructureGraph.solutionProjectPath

        let operationsReferences =
            ProjectStructureGraph.projectReferenceClosure rootProjects
            |> Array.filter (fun dependency -> dependency.ReferencedProject.Contains("Grace.Operations", StringComparison.OrdinalIgnoreCase))
            |> Array.map (fun dependency -> $"{dependency.ReferencingProject} -> {dependency.ReferencedProject} ({dependency.ReferencePath})")

        Assert.That(operationsReferences, Is.Empty)

    /// Proves project-reference graph walking handles Windows-style MSBuild includes on every runner OS.
    [<Test>]
    member _.``Project reference resolution normalizes Windows separators``() =
        let projectPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "RootProject", "RootProject.fsproj")

        let resolvedPath = ProjectStructureGraph.projectReferencePath projectPath @"..\Grace.Operations\Grace.Operations.fsproj"

        let expectedPath = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory, "Grace.Operations", "Grace.Operations.fsproj"))

        Assert.That(resolvedPath, Is.EqualTo(expectedPath))

    /// Proves Grace Server does not take a direct dependency on Operations application projects.
    [<Test>]
    member _.``Grace Server project does not reference Operations projects``() =
        let serverProject = ProjectStructureTestPaths.srcRelativePath "Grace.Server" "Grace.Server.fsproj"

        let operationsReferences =
            ProjectStructureXml.projectReferenceIncludes serverProject
            |> Array.filter (fun (includePath: string) -> includePath.Contains("Grace.Operations", StringComparison.OrdinalIgnoreCase))

        Assert.That(operationsReferences, Is.Empty)
