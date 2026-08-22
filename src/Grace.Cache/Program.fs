namespace Grace.Cache

open System
open System.Net
open System.Net.Http
open Grace.Cache.Storage
open Grace.Shared.Parameters.Cache
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http

/// Identifies the Cache host assembly to ASP.NET Core's in-process test factory.
type Program =
    class
    end

/// Carries only the opaque Server permit accepted by the Cache fill route.
[<CLIMutable>]
type CacheFillRequest = { Permit: string }

/// Composes the localhost-only HTTP boundary for verified Grace Cache artifacts.
module Host =

    /// Creates one redacted typed problem response for the Cache loopback contract.
    let private problem status code detail = Results.Json({ Code = code; Detail = detail }, statusCode = Nullable status)

    /// Starts the localhost-only HTTP boundary for verified Grace Cache artifacts.
    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)
        let configuredUrl = builder.Configuration["ASPNETCORE_URLS"]

        let listenPort =
            if String.IsNullOrWhiteSpace(configuredUrl) then
                0
            else
                let urls =
                    configuredUrl.Split(
                        ';',
                        StringSplitOptions.RemoveEmptyEntries
                        ||| StringSplitOptions.TrimEntries
                    )

                match urls with
                | [| url |] ->
                    match Uri.TryCreate(url, UriKind.Absolute) with
                    | true, uri -> uri.Port
                    | _ -> invalidOp "Grace.Cache accepts exactly one valid ASPNETCORE_URLS listener."
                | _ -> invalidOp "Grace.Cache accepts exactly one valid ASPNETCORE_URLS listener."

        builder.WebHost.ConfigureKestrel(fun options -> options.Listen(IPAddress.Loopback, listenPort))
        |> ignore

        let databasePath =
            match builder.Configuration["Cache:DatabasePath"] with
            | null -> invalidOp "Cache__DatabasePath is required."
            | value -> value

        let managedRoot =
            match builder.Configuration["Cache:ManagedRoot"] with
            | null -> invalidOp "Cache__ManagedRoot is required."
            | value -> value

        let maxConcurrentFills =
            match builder.Configuration["GRACE_CACHE_MAX_CONCURRENT_FILLS"] with
            | null -> 4
            | value ->
                match Int32.TryParse(value) with
                | true, parsed when parsed > 0 -> parsed
                | _ -> invalidOp "GRACE_CACHE_MAX_CONCURRENT_FILLS must be a positive integer."

        let serverUri =
            match builder.Configuration["GRACE_SERVER_URI"] with
            | null -> Uri("http://localhost:5000/")
            | value ->
                match Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute) with
                | true, uri -> uri
                | _ -> invalidOp "GRACE_SERVER_URI must be an absolute URI."

        let store =
            match CacheStore.openStore databasePath with
            | Opened opened -> opened
            | CacheDatabaseInUse -> invalidOp "Cache database is already owned by another process."

        let artifacts = CacheArtifactStore.create store managedRoot
        let processKey = CacheProcessKey.Create()
        let httpClient = new HttpClient()
        let coordinator = new CacheFillCoordinator(artifacts, processKey, serverUri, httpClient, maxConcurrentFills)
        let app = builder.Build()

        app.Lifetime.ApplicationStopped.Register (fun () ->
            (coordinator :> IDisposable).Dispose()
            httpClient.Dispose()
            (processKey :> IDisposable).Dispose()
            CacheStore.disposeStore store)
        |> ignore

        app.MapGet("/fill-public-key", Func<IResult>(fun () -> Results.Json(processKey.PublicJwk)))
        |> ignore

        app.MapGet(
            "/repositories/{repositoryId}/directory-version-zips/{directoryVersionId}",
            Func<string, string, IResult> (fun repositoryId directoryVersionId ->
                match Guid.TryParse(repositoryId), Guid.TryParse(directoryVersionId) with
                | (true, _), (true, _) ->
                    match CacheArtifactStore.inspectByIdentity artifacts repositoryId directoryVersionId with
                    | Hit finalPath -> Results.File(finalPath, "application/zip")
                    | Rejected message -> problem StatusCodes.Status400BadRequest "CacheRequestInvalid" message
                    | _ -> Results.NotFound()
                | _ -> problem StatusCodes.Status400BadRequest "CacheRequestInvalid" "Repository and directory version identities must be GUIDs.")
        )
        |> ignore

        app.MapPost(
            "/repositories/{repositoryId}/directory-version-zips/{directoryVersionId}/fill",
            Func<string, string, CacheFillRequest, Threading.Tasks.Task<IResult>> (fun repositoryId directoryVersionId request ->
                task {
                    if
                        not (fst (Guid.TryParse repositoryId))
                        || not (fst (Guid.TryParse directoryVersionId))
                    then
                        return problem StatusCodes.Status400BadRequest "CacheRequestInvalid" "Repository and directory version identities must be GUIDs."
                    elif
                        isNull (box request)
                        || String.IsNullOrWhiteSpace(request.Permit)
                    then
                        return problem StatusCodes.Status400BadRequest "CachePermitInvalid" "A fill permit is required."
                    else
                        match CacheArtifactStore.inspectByIdentity artifacts repositoryId directoryVersionId with
                        | Hit _ -> return Results.NoContent()
                        | RecoveryRequired _ ->
                            return problem StatusCodes.Status409Conflict "CacheRecoveryRequired" "Local Cache state requires an explicit reset."
                        | Conflict _ ->
                            return problem StatusCodes.Status409Conflict "CacheArtifactConflict" "A conflicting immutable tuple already owns this artifact."
                        | _ ->
                            match! coordinator.Fill(repositoryId, directoryVersionId, request.Permit) with
                            | Ok () ->
                                match CacheArtifactStore.inspectByIdentity artifacts repositoryId directoryVersionId with
                                | Hit _ -> return Results.NoContent()
                                | _ -> return problem StatusCodes.Status502BadGateway "CachePostFillVerificationFailed" "The committed artifact did not verify."
                            | Error CapacityExceeded ->
                                return problem StatusCodes.Status429TooManyRequests "CacheFillCapacityExceeded" "Distinct fill capacity is full."
                            | Error TupleConflict ->
                                return problem StatusCodes.Status409Conflict "CacheArtifactConflict" "A conflicting immutable tuple already owns this artifact."
                            | Error CacheFillError.RecoveryRequired ->
                                return problem StatusCodes.Status409Conflict "CacheRecoveryRequired" "Local Cache state requires an explicit reset."
                            | Error RedemptionFailed ->
                                return problem StatusCodes.Status403Forbidden "CachePermitRedemptionFailed" "Grace Server rejected the fill permit."
                            | Error SourceFailed -> return problem StatusCodes.Status502BadGateway "CacheSourceFailed" "The approved source could not be read."
                            | Error IntegrityFailed ->
                                return
                                    problem StatusCodes.Status422UnprocessableEntity "CacheIntegrityFailed" "The approved source did not match its descriptor."
                })
        )
        |> ignore

        app.Run()
        0
