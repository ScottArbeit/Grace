namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Types.ArtifactGrant
open Grace.Types.MaterializationPlan
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Security.Cryptography
open System.Threading.Tasks

/// Owns the durable deployment-wide key lifecycle used by artifact grants.
module ArtifactGrantSigningKeyActor =

    /// The fixed Orleans key that routes every server instance to one logical signing-key owner.
    [<Literal>]
    let DeploymentActorKey = "artifact-grant-signing-keys-v1"

    /// Coordinates persist-before-use key rotation and isolated cryptographic operations.
    type internal ArtifactGrantSigningKeyStore(state: IPersistentState<ArtifactGrantSigningKeyRingState>, log: ILogger) =

        let mutable initialized = false
        let mutable currentState = ArtifactGrantSigningKeyRingState.Empty

        /// Hydrates actor state only after Orleans has completed persistent-state loading.
        let ensureInitialized () =
            if not initialized then
                currentState <-
                    if state.RecordExists then
                        state.State
                    else
                        ArtifactGrantSigningKeyRingState.Empty

                initialized <- true

        /// Creates persisted P-256 private material with the approved active and overlap lifetimes.
        let createSigningKey (now: Instant) =
            let now = Instant.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds())
            use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            let keyId = $"agk-{ArtifactGrant.Base64Url.encode (RandomNumberGenerator.GetBytes 16)}"
            let activeUntil = now.Plus ArtifactGrantContract.SigningKeyActiveLifetime

            {
                KeyId = keyId
                CreatedAt = now
                ActiveUntil = activeUntil
                ExpiresAt = activeUntil.Plus ArtifactGrantContract.MaximumAcceptedGrantTtl
                PrivateKeyPkcs8 = key.ExportPkcs8PrivateKey()
            }

        /// Imports one persisted private key only when it is exactly compatible with the ES256/P-256 contract.
        let importPrivateKey (keyState: ArtifactGrantSigningKeyState) =
            let key = ECDsa.Create()
            let mutable bytesRead = 0

            try
                key.ImportPkcs8PrivateKey(keyState.PrivateKeyPkcs8, &bytesRead)

                if bytesRead <> keyState.PrivateKeyPkcs8.Length then
                    key.Dispose()
                    invalidOp "Artifact grant signing-key state contains trailing private-key bytes."

                let parameters = key.ExportParameters(true)
                let p256Oid = ECCurve.NamedCurves.nistP256.Oid.Value

                if parameters.Curve.Oid.Value <> p256Oid
                   || isNull parameters.Q.X
                   || parameters.Q.X.Length <> 32
                   || isNull parameters.Q.Y
                   || parameters.Q.Y.Length <> 32 then
                    key.Dispose()
                    invalidOp "Artifact grant signing-key state is not an ES256 P-256 private key."

                key
            with
            | ex ->
                key.Dispose()
                raise ex

        /// Rejects the complete durable ring when any retained entry cannot safely sign and publish as ES256/P-256.
        let requireValidState () =
            ensureInitialized ()

            let malformedEntry (keyState: ArtifactGrantSigningKeyState) =
                if isNull (box keyState)
                   || String.IsNullOrWhiteSpace keyState.KeyId
                   || isNull keyState.PrivateKeyPkcs8
                   || keyState.PrivateKeyPkcs8.Length = 0
                   || keyState.ActiveUntil <= keyState.CreatedAt
                   || keyState.ExpiresAt <= keyState.ActiveUntil
                   || keyState.ActiveUntil - keyState.CreatedAt
                      <> ArtifactGrantContract.SigningKeyActiveLifetime
                   || keyState.ExpiresAt - keyState.ActiveUntil
                      <> ArtifactGrantContract.MaximumAcceptedGrantTtl then
                    true
                else
                    try
                        use _ = importPrivateKey keyState
                        false
                    with
                    | _ -> true

            let malformedRing =
                isNull (box currentState)
                || currentState.Class
                   <> nameof ArtifactGrantSigningKeyRingState
                || isNull currentState.Keys

            let malformedKeys =
                not malformedRing
                && (currentState.Keys |> Array.exists malformedEntry
                    || (currentState.Keys
                        |> Array.map (fun key -> key.KeyId)
                        |> Array.distinct
                        |> Array.length)
                       <> currentState.Keys.Length)

            if malformedRing || malformedKeys then
                invalidOp "Artifact grant signing-key actor state is malformed."

        /// Writes a complete next state before exposing any key derived from it.
        let persist (nextState: ArtifactGrantSigningKeyRingState) =
            task {
                let previousState = state.State
                state.State <- nextState

                try
                    do! state.WriteStateAsync()
                    currentState <- nextState
                with
                | ex ->
                    state.State <- previousState
                    return raise ex
            }

        /// Purges expired keys and durably rotates unless one strict signer covers the required active horizon.
        let ensureActiveKey (now: Instant) (requiredActiveThrough: Instant) =
            task {
                requireValidState ()

                let liveKeys =
                    currentState.Keys
                    |> Array.filter (fun key ->
                        now
                        <= key.ExpiresAt.Plus ArtifactGrantContract.MaximumProofClockSkew)

                let activeKey =
                    liveKeys
                    |> Array.filter (fun key ->
                        key.CreatedAt <= now
                        && now < key.ActiveUntil
                        && requiredActiveThrough <= key.ActiveUntil)
                    |> Array.sortByDescending (fun key -> key.CreatedAt)
                    |> Array.tryHead

                let nextKeys, selectedKey, rotated =
                    match activeKey with
                    | Some key -> liveKeys, key, false
                    | None ->
                        let key = createSigningKey now
                        Array.append [| key |] liveKeys, key, true

                let changed =
                    nextKeys.Length <> currentState.Keys.Length
                    || rotated

                if changed then
                    let nextState = { Class = nameof ArtifactGrantSigningKeyRingState; Keys = nextKeys }
                    do! persist nextState
                    log.LogInformation("Persisted artifact grant signing-key lifecycle transition for key {KeyId}.", selectedKey.KeyId)

                return selectedKey
            }

        /// Validates and normalizes one actor signing request without trusting caller-supplied principal text.
        let validateRequest (request: ArtifactGrantSigningRequest) =
            if isNull (box request)
               || request.RequesterPrincipalType
                  <> ArtifactGrantRequesterPrincipalType.User
               || String.IsNullOrWhiteSpace request.RequesterPrincipalId then
                Error InvalidRequesterPrincipal
            else if not (ArtifactGrant.isValidHolderPublicKey request.HolderPublicKey) then
                Error InvalidHolderKeyThumbprint
            elif String.IsNullOrWhiteSpace request.CacheId then
                Error InvalidCacheId
            elif String.IsNullOrWhiteSpace request.CacheEndpoint then
                Error InvalidCacheEndpoint
            elif not (Grace.Types.MaterializationPlan.Validation.isAllowedCacheEndpoint request.CacheEndpoint) then
                Error InvalidCacheEndpoint
            elif request.TargetRootDirectoryVersionId = Guid.Empty then
                Error InvalidTargetRoot
            elif
                not (Grace.Types.MaterializationPlan.Validation.isSupportedExecutionMode request.ExecutionMode)
                || request.ExecutionMode = MaterializationExecutionMode.Direct
            then
                Error InvalidExecutionMode
            else
                let artifactIdentities =
                    if isNull request.ArtifactIdentities then
                        Array.empty
                    else
                        request.ArtifactIdentities
                        |> Array.map (fun identity -> if isNull identity then String.Empty else identity.Trim())
                        |> Array.filter (String.IsNullOrWhiteSpace >> not)
                        |> Array.distinct

                let ttl =
                    request.RequestedTtl
                    |> Option.defaultValue ArtifactGrantContract.DefaultGrantTtl

                if artifactIdentities.Length = 0 then
                    Error InvalidArtifactIdentities
                elif ttl <= Duration.Zero
                     || ttl > ArtifactGrantContract.MaximumAcceptedGrantTtl then
                    Error RequestedTtlTooLong
                else
                    Ok(artifactIdentities, ttl)

        /// Issues one grant only after the selected key is durably present in actor state.
        member _.IssueGrant(request: ArtifactGrantSigningRequest, now: Instant) =
            task {
                match validateRequest request with
                | Error error -> return Error error
                | Ok (artifactIdentities, ttl) ->
                    let! keyState = ensureActiveKey now now
                    use key = importPrivateKey keyState
                    let header = ArtifactGrantHeader.Create keyState.KeyId

                    let payload =
                        ArtifactGrantPayload.Create(
                            request.RequesterPrincipalId.Trim(),
                            ArtifactGrant.holderKeyThumbprint request.HolderPublicKey,
                            request.CacheId.Trim(),
                            request.CacheEndpoint.Trim(),
                            request.TargetRootDirectoryVersionId,
                            request.ExecutionMode,
                            artifactIdentities,
                            now,
                            ttl
                        )

                    return Ok(ArtifactGrant.sign key header payload)
            }

        /// Publishes only durably stored current and overlap keys using isolated crypto objects.
        member _.PublishValidationKeys(now: Instant) =
            task {
                let! _ = ensureActiveKey now (now.Plus ArtifactGrantContract.ValidationKeyCacheTtl)

                let keys =
                    currentState.Keys
                    |> Array.filter (fun key ->
                        now
                        <= key.ExpiresAt.Plus ArtifactGrantContract.MaximumProofClockSkew)
                    |> Array.map (fun keyState ->
                        use key = importPrivateKey keyState
                        ArtifactGrant.exportValidationKey keyState.KeyId keyState.CreatedAt keyState.ExpiresAt key)

                return ArtifactGrantValidationKeySet.Create(now, keys)
            }

    /// Serializes every signing, publication, rotation, retirement, and persistence operation through one Orleans grain.
    type ArtifactGrantSigningKeyActor
        (
            loggerFactory: ILoggerFactory,
            [<PersistentState(StateName.ArtifactGrantSigningKey, Grace.Shared.Constants.GraceActorStorage)>] state: IPersistentState<ArtifactGrantSigningKeyRingState>
        ) =
        inherit Grain()

        let store = ArtifactGrantSigningKeyStore(state, loggerFactory.CreateLogger("ArtifactGrantSigningKey.Actor"))

        interface IArtifactGrantSigningKeyActor with
            member _.IssueGrant(request, now) = store.IssueGrant(request, now)
            member _.PublishValidationKeys(now) = store.PublishValidationKeys now
