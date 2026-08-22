namespace Grace.Cache

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Net.Http.Json
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Grace.Cache.Storage
open Grace.Shared
open Grace.Shared.Parameters.Cache
open Grace.Types.Common

/// Encodes the unpadded base64url values used by JWK coordinates, permits, and signatures.
module internal Base64Url =

    /// Encodes bytes without padding using the URL-safe alphabet.
    let encode (bytes: byte array) =
        Convert
            .ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

/// Owns the one ephemeral Cache process key and its public JWK projection.
type CacheProcessKey private (key: ECDsa, publicJwk: CachePublicJwk) =

    /// Returns the public P-256 coordinates without private-key material.
    member _.PublicJwk = publicJwk

    /// Signs the exact opaque permit bytes using SHA-256 and IEEE P1363 encoding.
    member _.SignPermit(permit: string) =
        Encoding.UTF8.GetBytes(permit)
        |> fun bytes -> key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
        |> Base64Url.encode

    interface IDisposable with
        member _.Dispose() = key.Dispose()

    /// Generates a new P-256 identity for the current Cache process.
    static member Create() =
        let key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let parameters = key.ExportParameters(false)

        new CacheProcessKey(key, { Kty = "EC"; Crv = "P-256"; X = Base64Url.encode parameters.Q.X; Y = Base64Url.encode parameters.Q.Y })

/// Classifies terminal fill results without carrying secrets or managed paths.
type CacheFillError =
    | CapacityExceeded
    | RedemptionFailed
    | SourceFailed
    | IntegrityFailed
    | TupleConflict
    | RecoveryRequired

/// Coalesces exact fills and limits only distinct active network retrievals.
type CacheFillCoordinator(store: CacheArtifactStore, processKey: CacheProcessKey, serverUri: Uri, httpClient: HttpClient, maxConcurrentFills: int) =

    let capacity = new SemaphoreSlim(maxConcurrentFills, maxConcurrentFills)
    let fills = ConcurrentDictionary<string, Lazy<Task<Result<unit, CacheFillError>>>>(StringComparer.Ordinal)

    /// Redeems, retrieves, verifies, and commits one immutable artifact without honoring a disconnected request token.
    let execute repositoryId directoryVersionId permit =
        task {
            if not (capacity.Wait(0)) then
                return Error CapacityExceeded
            else
                try
                    let redemption = RedeemDirectoryVersionZipFillParameters()
                    redemption.Permit <- permit
                    redemption.Signature <- processKey.SignPermit permit

                    use! response =
                        httpClient.PostAsJsonAsync(
                            Uri(serverUri, "cache/redeemDirectoryVersionZipFill"),
                            redemption,
                            Constants.JsonSerializerOptions,
                            CancellationToken.None
                        )

                    if not response.IsSuccessStatusCode then
                        return Error RedemptionFailed
                    else
                        let! envelope =
                            response.Content.ReadFromJsonAsync<GraceReturnValue<DirectoryVersionZipFillSource>>(
                                Constants.JsonSerializerOptions,
                                CancellationToken.None
                            )

                        if isNull (box envelope) then
                            return Error RedemptionFailed
                        else
                            let source = envelope.ReturnValue
                            let descriptor = source.Descriptor

                            if descriptor.RepositoryId <> repositoryId
                               || descriptor.DirectoryVersionId
                                  <> directoryVersionId then
                                return Error RedemptionFailed
                            else
                                try
                                    use! sourceResponse = httpClient.GetAsync(Uri(source.SourceUri), HttpCompletionOption.ResponseHeadersRead)

                                    if not sourceResponse.IsSuccessStatusCode then
                                        return Error SourceFailed
                                    else
                                        use! sourceStream = sourceResponse.Content.ReadAsStreamAsync()

                                        let tuple =
                                            {
                                                Kind = descriptor.Kind
                                                CanonicalIdentity = CacheArtifactStore.canonicalIdentity repositoryId directoryVersionId
                                                DirectoryVersionId = directoryVersionId
                                                ExpectedSha256 = descriptor.Sha256
                                                ExpectedSize = descriptor.Size
                                            }

                                        match CacheArtifactStore.stage store tuple sourceStream with
                                        | Error _ -> return Error IntegrityFailed
                                        | Ok staged ->
                                            match CacheArtifactStore.publishStaged store staged with
                                            | Filled
                                            | Hit _ -> return Ok()
                                            | CacheArtifactOutcome.Conflict _ -> return Error TupleConflict
                                            | CacheArtifactOutcome.RecoveryRequired _ -> return Error CacheFillError.RecoveryRequired
                                            | CacheArtifactOutcome.Rejected _
                                            | CacheArtifactOutcome.Absent -> return Error IntegrityFailed
                                with
                                | :? HttpRequestException
                                | :? IOException -> return Error SourceFailed
                finally
                    capacity.Release() |> ignore
        }

    /// Joins one exact process-owned fill or starts it when distinct-fill capacity is available.
    member _.Fill(repositoryId: string, directoryVersionId: string, permit: string, waiterCancellation: CancellationToken) =
        task {
            let key = $"{repositoryId}/{directoryVersionId}"

            let operation = fills.GetOrAdd(key, (fun _ -> Lazy<Task<Result<unit, CacheFillError>>>(fun () -> execute repositoryId directoryVersionId permit)))
            let fill = operation.Value

            try
                return! fill.WaitAsync(waiterCancellation)
            finally
                if fill.IsCompleted then fills.TryRemove(key) |> ignore
        }

    /// Joins one exact process-owned fill without a detachable waiter token.
    member this.Fill(repositoryId: string, directoryVersionId: string, permit: string) =
        this.Fill(repositoryId, directoryVersionId, permit, CancellationToken.None)

    interface IDisposable with
        member _.Dispose() = capacity.Dispose()
