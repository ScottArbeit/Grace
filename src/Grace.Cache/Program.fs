namespace Grace.Cache

open System
open System.Net
open Grace.Cache.Storage
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http

/// Identifies the Cache host assembly to ASP.NET Core's in-process test factory.
type Program =
    class
    end

/// Composes the localhost-only HTTP boundary for verified Grace Cache artifacts.
module Host =

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

        let reader = CacheArtifactStore.createReader databasePath managedRoot
        let app = builder.Build()

        app.MapGet(
            "/directory-version-zips/{directoryVersionId}",
            Func<string, string, string, int64, IResult> (fun directoryVersionId canonicalIdentity sha256 size ->
                let tuple: CacheArtifactTuple =
                    {
                        Kind = "DirectoryVersionZip"
                        CanonicalIdentity = canonicalIdentity
                        DirectoryVersionId = directoryVersionId
                        ExpectedSha256 = sha256
                        ExpectedSize = size
                    }

                match CacheArtifactStore.read reader tuple with
                | Hit finalPath -> Results.File(finalPath, "application/zip")
                | Rejected message -> Results.BadRequest(message)
                | _ -> Results.NotFound())
        )
        |> ignore

        app.Run()
        0
