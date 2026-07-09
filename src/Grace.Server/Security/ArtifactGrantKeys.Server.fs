namespace Grace.Server.Security

open Grace.Shared
open Grace.Types.ArtifactGrant
open Grace.Types.Common
open Grace.Types.MaterializationPlan
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Security.Cryptography

/// Contains server-owned signing keys and artifact grant issuance for Grace Cache.
module ArtifactGrantKeys =

    /// Represents one server-owned artifact grant signing key.
    type private ArtifactGrantSigningKey = { KeyId: string; CreatedAt: Instant; ActiveUntil: Instant; ExpiresAt: Instant; Key: ECDsa }

    /// Represents a non-secret grant issuance failure.
    type ArtifactGrantIssueError =
        | InvalidCacheServicePrincipal
        | InvalidTargetRoot
        | InvalidExecutionMode
        | InvalidArtifactIdentities
        | RequestedTtlTooLong

    /// Contains non-secret error text for artifact grant issuance failures.
    [<RequireQualifiedAccess>]
    module ArtifactGrantIssueError =

        /// Converts an issuance failure to a diagnostic message without raw grant or signing material.
        let toMessage error =
            match error with
            | InvalidCacheServicePrincipal -> "Cache service principal id is required."
            | InvalidTargetRoot -> "Target root DirectoryVersionId is required."
            | InvalidExecutionMode -> "Artifact grant execution mode is not supported."
            | InvalidArtifactIdentities -> "Artifact grants must bind at least one explicit artifact identity."
            | RequestedTtlTooLong -> "Artifact grant TTL exceeds the accepted maximum."

    /// Represents the inputs needed to issue one signed artifact grant.
    type ArtifactGrantIssueRequest =
        {
            CacheServicePrincipalId: string
            TargetRootDirectoryVersionId: DirectoryVersionId
            ExecutionMode: MaterializationExecutionMode
            ArtifactIdentities: string seq
            RequestedTtl: Duration option
        }

    /// Maintains process-local artifact grant signing keys and publishes validation keys.
    type ArtifactGrantKeyRing(log: ILogger<ArtifactGrantKeyRing>) =
        let syncRoot = obj ()
        let mutable signingKeys: ArtifactGrantSigningKey list = []

        /// Creates an opaque key id for a server-owned signing key without exposing signing material.
        let createKeyId () =
            let bytes = RandomNumberGenerator.GetBytes 16
            $"agk-{ArtifactGrant.Base64Url.encode bytes}"

        /// Creates one new P-256 signing key with overlap long enough for issued grants to expire.
        let createSigningKey (now: Instant) =
            let activeUntil = now.Plus ArtifactGrantContract.SigningKeyActiveLifetime

            {
                KeyId = createKeyId ()
                CreatedAt = now
                ActiveUntil = activeUntil
                ExpiresAt = activeUntil.Plus ArtifactGrantContract.MaximumAcceptedGrantTtl
                Key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            }

        /// Returns keys that can still validate grants at the supplied server time.
        let currentValidationKeys now =
            signingKeys
            |> List.filter (fun key -> now < key.ExpiresAt)

        /// Removes expired signing keys and disposes their private key material.
        let purgeExpired now =
            let live, expired =
                signingKeys
                |> List.partition (fun key -> now < key.ExpiresAt)

            expired
            |> List.iter (fun key -> key.Key.Dispose())

            signingKeys <- live

        /// Gets the active signing key, rotating if the current active key lifetime has ended.
        member private _.GetCurrentSigningKey(now: Instant) =
            lock syncRoot (fun () ->
                purgeExpired now

                match signingKeys
                      |> List.tryFind (fun key -> now < key.ActiveUntil)
                    with
                | Some key -> key
                | None ->
                    let key = createSigningKey now
                    signingKeys <- key :: signingKeys
                    log.LogInformation("Created artifact grant signing key {KeyId}.", key.KeyId)
                    key)

        /// Publishes current and overlap validation keys without private signing material.
        member _.PublishValidationKeys(now: Instant) =
            lock syncRoot (fun () ->
                purgeExpired now

                if signingKeys.IsEmpty then
                    let key = createSigningKey now
                    signingKeys <- [ key ]
                    log.LogInformation("Created artifact grant signing key {KeyId}.", key.KeyId)

                currentValidationKeys now
                |> Seq.map (fun key -> ArtifactGrant.exportValidationKey key.KeyId key.CreatedAt key.ExpiresAt key.Key)
                |> fun keys -> ArtifactGrantValidationKeySet.Create(now, keys))

        /// Issues one signed artifact grant using the active server-owned signing key.
        member this.IssueGrant(now: Instant, request: ArtifactGrantIssueRequest) =
            if
                isNull (box request)
                || String.IsNullOrWhiteSpace request.CacheServicePrincipalId
            then
                Error InvalidCacheServicePrincipal
            elif request.TargetRootDirectoryVersionId = Guid.Empty then
                Error InvalidTargetRoot
            elif not (Validation.isSupportedExecutionMode request.ExecutionMode) then
                Error InvalidExecutionMode
            elif request.ExecutionMode = MaterializationExecutionMode.Direct then
                Error InvalidExecutionMode
            else
                let artifactIdentities =
                    if isNull (box request.ArtifactIdentities) then
                        Array.empty
                    else
                        request.ArtifactIdentities
                        |> Seq.map (fun identity -> if isNull identity then String.Empty else identity.Trim())
                        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
                        |> Seq.distinct
                        |> Seq.toArray

                let ttl =
                    request.RequestedTtl
                    |> Option.defaultValue ArtifactGrantContract.DefaultGrantTtl

                if artifactIdentities.Length = 0 then
                    Error InvalidArtifactIdentities
                elif ttl <= Duration.Zero
                     || ttl > ArtifactGrantContract.MaximumAcceptedGrantTtl then
                    Error RequestedTtlTooLong
                else
                    let signingKey = this.GetCurrentSigningKey now
                    let header = ArtifactGrantHeader.Create signingKey.KeyId

                    let payload =
                        ArtifactGrantPayload.Create(
                            request.CacheServicePrincipalId.Trim(),
                            request.TargetRootDirectoryVersionId,
                            request.ExecutionMode,
                            artifactIdentities,
                            now,
                            ttl
                        )

                    Ok(ArtifactGrant.sign signingKey.Key header payload)
