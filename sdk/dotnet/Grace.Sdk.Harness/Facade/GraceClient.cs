namespace Grace.Sdk.Harness;

/// <summary>
/// Minimal facade placeholder for the extraction-compatible .NET SDK package lane.
/// </summary>
public sealed class GraceClient
{
    public string ApiContractVersion => Internal.Generated.GraceRawClientMetadata.ApiContractVersion;

    public string OpenApiProjectionSha256 => Internal.Generated.GraceRawClientMetadata.OpenApiProjectionSha256;
}
