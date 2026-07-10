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

        /// Rejects malformed persisted state instead of creating a process-local replacement key.
        let requireValidState () =
            ensureInitialized ()

            if isNull (box currentState)
               || currentState.Class
                  <> nameof ArtifactGrantSigningKeyRingState
               || isNull currentState.Keys
               || currentState.Keys
                  |> Array.exists (fun key ->
                      isNull (box key)
                      || String.IsNullOrWhiteSpace key.KeyId
                      || isNull key.PrivateKeyPkcs8
                      || key.PrivateKeyPkcs8.Length = 0
                      || key.ActiveUntil <= key.CreatedAt
                      || key.ExpiresAt <= key.ActiveUntil) then
                invalidOp "Artifact grant signing-key actor state is malformed."

        /// Creates persisted P-256 private material with the approved active and overlap lifetimes.
        let createSigningKey (now: Instant) =
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

        /// Imports one persisted private key into an operation-local cryptographic object.
        let importPrivateKey (keyState: ArtifactGrantSigningKeyState) =
            let key = ECDsa.Create()
            let mutable bytesRead = 0

            try
                key.ImportPkcs8PrivateKey(keyState.PrivateKeyPkcs8, &bytesRead)

                if bytesRead <> keyState.PrivateKeyPkcs8.Length then
                    key.Dispose()
                    invalidOp "Artifact grant signing-key state contains trailing private-key bytes."

                key
            with
            | ex ->
                key.Dispose()
                raise ex

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

        /// Purges expired keys and rotates when no active key remains, persisting before return.
        let ensureActiveKey (now: Instant) =
            task {
                requireValidState ()

                let liveKeys =
                    currentState.Keys
                    |> Array.filter (fun key -> now < key.ExpiresAt)

                let activeKey =
                    liveKeys
                    |> Array.filter (fun key -> key.CreatedAt <= now && now < key.ActiveUntil)
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
            elif String.IsNullOrWhiteSpace request.CacheServicePrincipalId then
                Error InvalidCacheServicePrincipal
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
                    let! keyState = ensureActiveKey now
                    use key = importPrivateKey keyState
                    let header = ArtifactGrantHeader.Create keyState.KeyId

                    let payload =
                        ArtifactGrantPayload.Create(
                            request.RequesterPrincipalId.Trim(),
                            ArtifactGrant.holderKeyThumbprint request.HolderPublicKey,
                            request.CacheServicePrincipalId.Trim(),
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
                let! _ = ensureActiveKey now

                let keys =
                    currentState.Keys
                    |> Array.filter (fun key -> now < key.ExpiresAt)
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
