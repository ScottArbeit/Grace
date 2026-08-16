using System.Net;
using Grace.Cache.Storage;

var builder = WebApplication.CreateBuilder(args);
var configuredUrl = builder.Configuration["ASPNETCORE_URLS"];
var listenPort = 0;
if (!string.IsNullOrWhiteSpace(configuredUrl))
{
    var urls = configuredUrl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (urls.Length != 1 || !Uri.TryCreate(urls[0], UriKind.Absolute, out var uri))
        throw new InvalidOperationException("Grace.Cache accepts exactly one valid ASPNETCORE_URLS listener.");
    listenPort = uri.Port;
}
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, listenPort));

var databasePath = builder.Configuration["Cache:DatabasePath"]
    ?? throw new InvalidOperationException("Cache__DatabasePath is required.");
var managedRoot = builder.Configuration["Cache:ManagedRoot"]
    ?? throw new InvalidOperationException("Cache__ManagedRoot is required.");
var reader = CacheArtifactStoreModule.createReader(databasePath, managedRoot);

var app = builder.Build();

app.MapGet(
    "/directory-version-zips/{directoryVersionId}",
    (string directoryVersionId, string canonicalIdentity, string sha256, long size) =>
    {
        var tuple = new CacheArtifactTuple(
            "DirectoryVersionZip",
            canonicalIdentity,
            directoryVersionId,
            sha256,
            size);

        return CacheArtifactStoreModule.read(reader, tuple) switch
        {
            CacheArtifactOutcome.Hit hit => Results.File(hit.finalPath, "application/zip"),
            CacheArtifactOutcome.Rejected rejected => Results.BadRequest(rejected.message),
            _ => Results.NotFound(),
        };
    });

app.Run();

/// <summary>Exposes the localhost Cache host entry point to focused in-process HTTP tests.</summary>
public partial class Program;
