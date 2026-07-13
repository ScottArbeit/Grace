namespace Grace.Authorization.Tests

open Grace.Shared
open Grace.Server.Security.EndpointAuthorizationManifest
open Grace.Types.Authorization
open NUnit.Framework
open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

/// Contains tests covering route behavior.
type Route = { Method: string; Path: string }

/// Contains tests covering classification group behavior.
type ClassificationGroup = { Classification: string; Reason: string; TraceIds: string list; Routes: Route list }

/// Contains tests covering OpenAPI route coverage behavior.
[<Parallelizable(ParallelScope.All)>]
type OpenApiRouteCoverageTests() =

    let openApiMainPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "OpenAPI", "Main.OpenAPI.yaml"))
    let cachePathsSourcePath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "OpenAPI", "Cache.Paths.OpenAPI.yaml"))
    let openApiBundlePath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "OpenAPI", "Grace.OpenAPI.yaml"))
    let openApiProjectionPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "OpenAPI", "Grace.OpenAPI.3.1.2.yaml"))
    let routeClassificationRegistryPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "OpenAPI", "RouteClassification.json"))
    let startupPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Startup.Server.fs"))

    let openApiPathRegex = Regex("^  (?<path>/[^:]+):\\s*$", RegexOptions.Compiled)
    let openApiVersionRegex = Regex("""^  version: "(?<version>[^"]+)"\s*$""", RegexOptions.Compiled)

    let startupRouteTokenRegex =
        Regex("(?<method>\\bGET\\b|\\bPOST\\b|\\bPUT\\b)\\s*\\[|(?<kind>subRoute|routef|route)\\s+\"(?<path>[^\"]+)\"", RegexOptions.Multiline)

    /// Exercises route coverage for the authorization OpenAPI route coverage contract.
    let route method_ path = { Method = method_; Path = path }

    /// Formats route.
    let formatRoute route = $"{route.Method} {route.Path}"

    /// Formats routes.
    let formatRoutes routes =
        routes
        |> Seq.sortBy (fun route -> route.Method, route.Path)
        |> Seq.map formatRoute
        |> String.concat Environment.NewLine

    /// Exercises fail with routes coverage for the authorization OpenAPI route coverage contract.
    let failWithRoutes message routes = Assert.Fail($"{message}:{Environment.NewLine}{formatRoutes routes}")

    /// Exercises OpenAPI version coverage for the authorization OpenAPI route coverage contract.
    let openApiVersion () =
        File.ReadAllLines(openApiMainPath)
        |> Array.pick (fun line ->
            let matchItem = openApiVersionRegex.Match(line)

            if matchItem.Success then Some matchItem.Groups["version"].Value else None)

    /// Gets one top-level OpenAPI path block from canonical or generated contract text.
    let getOpenApiPathBlock artifactPath path =
        let text = File.ReadAllText artifactPath
        let pathPattern = Regex.Escape path
        let pathBlock = Regex.Match(text, $"(?ms)^  {pathPattern}:\\s*\\r?\\n(?<block>.*?)(?=^  /|\\z)")

        if not pathBlock.Success then
            Assert.Fail($"{Path.GetFileName artifactPath} is missing the {path} path block.")

        pathBlock.Groups["block"].Value

    /// Exercises OpenAPI paths coverage for the authorization OpenAPI route coverage contract.
    let openApiPaths () =
        File.ReadAllLines(openApiMainPath)
        |> Array.choose (fun line ->
            let matchItem = openApiPathRegex.Match(line)

            if matchItem.Success then Some matchItem.Groups["path"].Value else None)
        |> Set.ofArray

    /// Asserts bundled schema is unique.
    let assertBundledSchemaIsUnique artifactPath schemaName =
        let lines = File.ReadAllLines(artifactPath)
        let schemaLine = $"    {schemaName}:"
        let externalRefLine = $"      $ref: Branch.Components.OpenAPI.yaml#/{schemaName}"

        let schemaIndexes =
            lines
            |> Array.mapi (fun index line -> index, line)
            |> Array.choose (fun (index, line) ->
                if String.Equals(line, schemaLine, StringComparison.Ordinal) then
                    Some index
                else
                    None)

        match schemaIndexes with
        | [| schemaIndex |] ->
            /// Exercises preceding index coverage for the authorization OpenAPI route coverage contract.
            let precedingIndex expectedLine =
                lines[..schemaIndex]
                |> Array.mapi (fun index line -> index, line)
                |> Array.choose (fun (index, line) ->
                    if String.Equals(line, expectedLine, StringComparison.Ordinal) then
                        Some index
                    else
                        None)
                |> Array.last

            let precedingSchemasIndex = precedingIndex "  schemas:"
            let precedingComponentsIndex = precedingIndex "components:"

            Assert.That(precedingSchemasIndex, Is.GreaterThan(precedingComponentsIndex), $"{schemaName} must be nested under components.schemas.")
            Assert.That(lines, Does.Not.Contain(externalRefLine), $"{Path.GetFileName artifactPath} must not keep an external {schemaName} schema ref.")
        | [||] -> Assert.Fail($"{Path.GetFileName artifactPath} must define {schemaName} under components.schemas.")
        | _ -> Assert.Fail($"{Path.GetFileName artifactPath} must define {schemaName} only once under components.schemas.")

    /// Parses startup routes.
    let parseStartupRoutes () =
        let text = File.ReadAllText(startupPath)
        let matches = startupRouteTokenRegex.Matches(text)
        /// Tracks current Prefix changes so this scenario can assert the resulting side effect explicitly.
        let mutable currentPrefix = String.Empty
        /// Tracks current Method changes so this scenario can assert the resulting side effect explicitly.
        let mutable currentMethod = String.Empty
        let routes = ResizeArray<Route>()

        for matchItem in matches do
            if matchItem.Groups["method"].Success then
                currentMethod <- matchItem.Groups["method"].Value
            elif matchItem.Groups["kind"].Success then
                let kind = matchItem.Groups["kind"].Value
                let path = matchItem.Groups["path"].Value

                if kind = "subRoute" then
                    currentPrefix <- path
                else if String.IsNullOrWhiteSpace currentMethod then
                    invalidOp $"Missing HTTP method before route '{path}'."
                else
                    let fullPath =
                        if String.IsNullOrWhiteSpace currentPrefix then
                            path
                        else
                            $"{currentPrefix}{path}"

                    routes.Add(route currentMethod fullPath)

        routes
        |> Seq.toList
        |> List.append [ route "GET" "/metrics"
                         route "GET" "/notifications" ]

    /// Gets required string.
    let getRequiredString (parent: JsonElement) (propertyName: string) =
        let value = parent.GetProperty(propertyName).GetString()

        if String.IsNullOrWhiteSpace value then
            invalidOp $"RouteClassification.json entry has a blank '{propertyName}' value."

        value

    /// Exercises route classification groups coverage for the authorization OpenAPI route coverage contract.
    let routeClassificationGroups () =
        use document = JsonDocument.Parse(File.ReadAllText(routeClassificationRegistryPath))

        document
            .RootElement
            .GetProperty("classifications")
            .EnumerateArray()
        |> Seq.map (fun classificationElement ->
            let traceIds =
                classificationElement
                    .GetProperty("traceIds")
                    .EnumerateArray()
                |> Seq.map (fun traceIdElement ->
                    let traceId = traceIdElement.GetString()

                    if String.IsNullOrWhiteSpace traceId then
                        invalidOp "RouteClassification.json entry has a blank trace id."

                    traceId)
                |> Seq.toList

            let routes =
                classificationElement
                    .GetProperty("routes")
                    .EnumerateArray()
                |> Seq.map (fun routeElement -> { Method = getRequiredString routeElement "method"; Path = getRequiredString routeElement "path" })
                |> Seq.toList

            {
                Classification = getRequiredString classificationElement "classification"
                Reason = getRequiredString classificationElement "reason"
                TraceIds = traceIds
                Routes = routes
            })
        |> Seq.toList

    /// Asserts route security.
    let assertRouteSecurity method_ path (expectedSecurity: EndpointSecurity) =
        let matchingDefinitions =
            definitions
            |> List.filter (fun definition ->
                definition.Method = method_
                && definition.Path = path)

        match matchingDefinitions with
        | [ definition ] ->
            Assert.That(
                definition.Security,
                Is.EqualTo(expectedSecurity),
                $"Expected {method_} {path} to use {expectedSecurity}, but manifest uses {definition.Security}."
            )
        | [] -> Assert.Fail($"Expected EndpointAuthorizationManifest to include {method_} {path}.")
        | _ -> Assert.Fail($"Expected EndpointAuthorizationManifest to include one entry for {method_} {path}.")

    /// Verifies that OpenAPI covers adr storage routes.
    [<Test>]
    member _.OpenApiCoversAdrStorageRoutes() =
        let expectedStorageRoutes =
            [
                "/storage/getUploadMetadataForFiles"
                "/storage/getUploadUri"
                "/storage/getDownloadUri"
                "/storage/getContentBlockUploadUri"
                "/storage/getContentBlockDownloadUri"
                "/storage/discoverContentBlocks"
                "/storage/startManifestUploadSession"
                "/storage/issueDedupeDiscovery"
                "/storage/claimReuseRanges"
                "/storage/registerContentBlockUpload"
                "/storage/confirmContentBlockUpload"
                "/storage/finalizeManifestUpload"
            ]
            |> Set.ofList

        let missing = expectedStorageRoutes - openApiPaths ()

        if missing.Count > 0 then
            let missingText =
                missing
                |> Seq.sort
                |> String.concat Environment.NewLine

            Assert.Fail($"OpenAPI is missing ADR-0001 storage routes:{Environment.NewLine}{missingText}")

    /// Verifies that bundled OpenAPI branch request schemas remain unique under components schemas.
    [<Test>]
    member _.BundledOpenApiBranchRequestSchemasRemainUniqueUnderComponentsSchemas() =
        for artifactPath in
            [
                openApiBundlePath
                openApiProjectionPath
            ] do
            assertBundledSchemaIsUnique artifactPath "GetReferencesParameters"
            assertBundledSchemaIsUnique artifactPath "AnnotateParameters"

    /// Verifies proof-only Cache operations override global bearer security in canonical and generated OpenAPI contracts.
    [<Test>]
    member _.CacheProofOperationsOverrideBearerSecurityWhileAdministratorOperationsRemainProtected() =
        let proofOnlyPaths =
            [
                "/cache/validation-keys"
                "/cache/refresh"
                "/cache/rotate-key"
            ]

        let administratorPaths =
            [
                "/cache/enroll"
                "/cache/assign-repositories"
                "/cache/revoke"
            ]

        for artifactPath in
            [
                cachePathsSourcePath
                openApiBundlePath
                openApiProjectionPath
            ] do
            for path in proofOnlyPaths do
                Assert.That(
                    getOpenApiPathBlock artifactPath path,
                    Does.Contain "      security: []",
                    $"{Path.GetFileName artifactPath} must publish {path} without inherited bearer authentication."
                )

            for path in administratorPaths do
                Assert.That(
                    getOpenApiPathBlock artifactPath path,
                    Does.Not.Contain "      security: []",
                    $"{Path.GetFileName artifactPath} must retain the global bearer requirement for {path}."
                )

    /// Verifies that OpenAPI info version matches current api contract version.
    [<Test>]
    member _.OpenApiInfoVersionMatchesCurrentApiContractVersion() = Assert.That(openApiVersion (), Is.EqualTo(ApiContractVersion.CurrentReleased))

    /// Verifies that route classification registry uses known classifications and has no duplicate routes.
    [<Test>]
    member _.RouteClassificationRegistryUsesKnownClassificationsAndHasNoDuplicateRoutes() =
        let knownClassifications =
            [
                "debugTestOnlyRoute"
                "incompleteUnimplementedSurface"
                "internalOnlyContract"
                "intentionallyExcludedOperationalSurface"
                "staleArtifact"
            ]
            |> Set.ofList

        let groups = routeClassificationGroups ()

        for group in groups do
            Assert.That(
                knownClassifications.Contains group.Classification,
                Is.True,
                $"RouteClassification.json uses unknown classification '{group.Classification}'."
            )

            Assert.That(group.Reason.Trim().Length, Is.GreaterThan(0), $"Route classification '{group.Classification}' has a blank reason.")
            Assert.That(group.TraceIds, Is.Not.Empty, $"Route classification '{group.Classification}' has no trace ids.")
            Assert.That(group.Routes, Is.Not.Empty, $"Route classification '{group.Classification}' has no routes.")

        let duplicates =
            groups
            |> List.collect (fun group -> group.Routes)
            |> List.groupBy id
            |> List.choose (fun (route, entries) -> if entries.Length > 1 then Some route else None)

        if not duplicates.IsEmpty then
            failWithRoutes "RouteClassification.json classifies routes more than once" duplicates

    /// Verifies that route classification registry covers implemented manifest and OpenAPI surfaces.
    [<Test>]
    member _.RouteClassificationRegistryCoversImplementedManifestAndOpenApiSurfaces() =
        let startupRoutes = parseStartupRoutes () |> Set.ofList

        let manifestRoutes =
            definitions
            |> List.map (fun definition -> route definition.Method definition.Path)
            |> Set.ofList

        let missingFromManifest = startupRoutes - manifestRoutes

        if missingFromManifest.Count > 0 then
            failWithRoutes "EndpointAuthorizationManifest is missing Startup.Server.fs routes" missingFromManifest

        let extraInManifest = manifestRoutes - startupRoutes

        if extraInManifest.Count > 0 then
            failWithRoutes "EndpointAuthorizationManifest includes routes not in Startup.Server.fs" extraInManifest

        let groups = routeClassificationGroups ()
        let openApiPaths = openApiPaths ()

        let registryNonPublicRoutes =
            groups
            |> List.filter (fun group -> group.Classification <> "staleArtifact")
            |> List.collect (fun group -> group.Routes)
            |> Set.ofList

        let implementedRoutesAbsentFromOpenApi =
            startupRoutes
            |> Set.filter (fun route -> not (openApiPaths.Contains route.Path))

        let missingRegistryClassifications =
            implementedRoutesAbsentFromOpenApi
            - registryNonPublicRoutes

        if missingRegistryClassifications.Count > 0 then
            failWithRoutes "Implemented non-OpenAPI routes need explicit RouteClassification.json exclusion reasons" missingRegistryClassifications

        let registryRoutesNotImplemented =
            registryNonPublicRoutes
            - implementedRoutesAbsentFromOpenApi

        if registryRoutesNotImplemented.Count > 0 then
            failWithRoutes "RouteClassification.json non-public routes must match implemented non-OpenAPI routes" registryRoutesNotImplemented

        let publicRoutesAlsoClassifiedNonPublic =
            startupRoutes
            |> Set.filter (fun route -> openApiPaths.Contains route.Path)
            |> Set.intersect registryNonPublicRoutes

        if publicRoutesAlsoClassifiedNonPublic.Count > 0 then
            failWithRoutes "Implemented routes present in OpenAPI must not also be classified as non-public" publicRoutesAlsoClassifiedNonPublic

    /// Verifies that OpenAPI stale paths are explicitly classified.
    [<Test>]
    member _.OpenApiStalePathsAreExplicitlyClassified() =
        let implementedPaths =
            parseStartupRoutes ()
            |> Seq.map (fun route -> route.Path)
            |> Set.ofSeq

        let staleOpenApiPaths = openApiPaths () - implementedPaths

        let staleRegistryPaths =
            routeClassificationGroups ()
            |> List.filter (fun group -> group.Classification = "staleArtifact")
            |> List.collect (fun group -> group.Routes)
            |> List.map (fun route -> route.Path)
            |> Set.ofList

        let missingStaleClassifications = staleOpenApiPaths - staleRegistryPaths

        if missingStaleClassifications.Count > 0 then
            let missingText =
                missingStaleClassifications
                |> Seq.sort
                |> String.concat Environment.NewLine

            Assert.Fail($"OpenAPI paths not implemented by Startup.Server.fs must be classified as stale:{Environment.NewLine}{missingText}")

        Assert.That(staleRegistryPaths.Contains "/openApi", Is.True, "The legacy /openApi OpenAPI path must remain explicitly classified as stale.")

    /// Verifies that non OpenAPI routes have explicit exclusion reasons.
    [<Test>]
    member _.NonOpenApiRoutesHaveExplicitExclusionReasons() =
        let groups =
            routeClassificationGroups ()
            |> List.filter (fun group -> group.Classification <> "staleArtifact")

        let routesWithoutExplanatoryReasons =
            groups
            |> List.filter (fun group -> String.IsNullOrWhiteSpace group.Reason)
            |> List.collect (fun group -> group.Routes)

        if not routesWithoutExplanatoryReasons.IsEmpty then
            failWithRoutes "Non-OpenAPI routes need machine-readable exclusion reasons" routesWithoutExplanatoryReasons

    /// Verifies that debug admin routes require system admin and stay out of OpenAPI.
    [<Test>]
    member _.DebugAdminRoutesRequireSystemAdminAndStayOutOfOpenApi() =
        let openApiPaths = openApiPaths ()
        let groups = routeClassificationGroups ()

        let debugAdminRoutes =
            groups
            |> List.collect (fun group ->
                group.Routes
                |> List.map (fun route -> group.Classification, route))
            |> List.choose (fun (classification, route) ->
                if
                    classification = "debugTestOnlyRoute"
                    && route.Path.StartsWith("/admin/", StringComparison.Ordinal)
                then
                    Some route
                else
                    None)

        for route in debugAdminRoutes do
            Assert.That(openApiPaths.Contains route.Path, Is.False, $"{formatRoute route} must not be silently public in OpenAPI.")
            assertRouteSecurity route.Method route.Path (Authorized(Operation.SystemAdmin, ResourceKind.System))

        Assert.That(openApiPaths.Contains "/metrics", Is.False, "GET /metrics is an operational surface, not a public OpenAPI path.")
        assertRouteSecurity "GET" "/metrics" (Authorized(Operation.SystemAdmin, ResourceKind.System))
