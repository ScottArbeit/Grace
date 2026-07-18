namespace Grace.Cache.Tests

open System
open System.Net.Sockets
open System.Security.Cryptography
open System.Text.Json
open Grace.Cache
open Grace.Shared.ArtifactGrant
open Grace.Shared
open Grace.Types.CacheRegistration
open NUnit.Framework

/// Verifies cache-process control verbs reach only the cache runtime boundary.
[<TestFixture>]
type CacheProcessDispatchTests() =
    /// Builds one complete enrollment argument vector with explicit repository-to-organization pairing.
    let enrollmentArguments =
        [|
            "--enroll"
            "--endpoint"
            "https://cache.example.test"
            "--display-name"
            "edge-cache"
            "--owner-id"
            "11111111-1111-1111-1111-111111111111"
            "--repository-id"
            "33333333-3333-3333-3333-333333333333"
            "--repository-organization-id"
            "22222222-2222-2222-2222-222222222222"
        |]

    /// Verifies an absent or invalid private process marker rejects every verb before the process dispatch boundary can cause an effect.
    [<Test>]
    member _.InvalidProcessMarkerHasZeroRuntimeEffects() =
        let mutable effectsCalled = 0

        let effects: CacheProcessEffects =
            {
                Enroll =
                    fun _ ->
                        effectsCalled <- effectsCalled + 1
                        Ok(CacheRuntimeStatus.registered Guid.Empty "https")
                Status =
                    fun () ->
                        effectsCalled <- effectsCalled + 1
                        Ok(CacheRuntimeStatus.registered Guid.Empty "https")
                Run =
                    fun () ->
                        effectsCalled <- effectsCalled + 1
                        Ok(CacheRuntimeStatus.registered Guid.Empty "https")
            }

        let missing = Program.executeWithMarker null effects [| "--status" |]
        let invalid = Program.executeWithMarker "look-alike" effects [| "--run" |]

        Assert.That(missing.ExitCode, Is.EqualTo(1))
        Assert.That(invalid.ExitCode, Is.EqualTo(1))
        Assert.That(missing.Payload, Is.EqualTo(invalid.Payload))
        Assert.That(effectsCalled, Is.EqualTo(0))

    /// Verifies enrollment reaches the runtime boundary once with canonical explicit input.
    [<Test>]
    member _.EnrollmentDispatchesToRuntimeBoundary() =
        let mutable received = None

        let effects: CacheProcessEffects =
            {
                Enroll =
                    fun input ->
                        received <- Some input
                        Ok(CacheRuntimeStatus.registered (Guid.Parse "44444444-4444-4444-4444-444444444444") "https")
                Status = fun () -> failwith "Status must not run for enrollment."
                Run = fun () -> failwith "Host startup must not run for enrollment."
            }

        let result = CacheProcessCommand.execute effects enrollmentArguments

        Assert.That(result.ExitCode, Is.EqualTo(0))
        Assert.That(Option.isSome received, Is.True)
        Assert.That(result.Payload, Does.Not.Contain("private"))

    /// Verifies malformed enrollment is rejected before the runtime can create a key, write configuration, or call Grace Server.
    [<Test>]
    member _.MalformedEnrollmentHasNoRuntimeSideEffects() =
        let mutable sideEffects = 0

        let effects: CacheProcessEffects =
            {
                Enroll =
                    fun _ ->
                        sideEffects <- sideEffects + 1
                        failwith "Malformed enrollment must not reach the runtime boundary."
                Status = fun () -> failwith "Status must not run for enrollment."
                Run = fun () -> failwith "Host startup must not run for enrollment."
            }

        let result =
            CacheProcessCommand.execute
                effects
                [|
                    "--enroll"
                    "--endpoint"
                    "https://cache.example.test"
                |]

        Assert.That(result.ExitCode, Is.EqualTo(1))
        Assert.That(sideEffects, Is.EqualTo(0))
        Assert.That(result.Payload, Does.Not.Contain("https://cache.example.test"))

    /// Verifies endpoint user information is rejected before key creation, recovery persistence, or server enrollment effects.
    [<TestCase("https://operator@cache.example.test/")>]
    [<TestCase("https://operator:secret@cache.example.test/")>]
    member _.EnrollmentRejectsEndpointUserInformationBeforeRuntimeEffects(endpoint) =
        let mutable sideEffects = 0

        let effects: CacheProcessEffects =
            {
                Enroll =
                    fun _ ->
                        sideEffects <- sideEffects + 1
                        failwith "Endpoint user information must not reach enrollment."
                Status = fun () -> failwith "Status must not run for enrollment."
                Run = fun () -> failwith "Host startup must not run for enrollment."
            }

        let arguments =
            enrollmentArguments
            |> Array.map (fun argument -> if argument = "https://cache.example.test" then endpoint else argument)

        let result = CacheProcessCommand.execute effects arguments

        Assert.That(result.ExitCode, Is.EqualTo(1))
        Assert.That(sideEffects, Is.EqualTo(0))
        Assert.That(result.Payload, Does.Not.Contain("operator"))
        Assert.That(result.Payload, Does.Not.Contain("secret"))

    /// Verifies unrecognized process options cannot silently broaden enrollment input before any runtime side effect.
    [<Test>]
    member _.UnknownEnrollmentOptionHasNoRuntimeSideEffects() =
        let effects: CacheProcessEffects =
            {
                Enroll = fun _ -> failwith "Unknown enrollment input must not reach the runtime boundary."
                Status = fun () -> failwith "Status must not run for enrollment."
                Run = fun () -> failwith "Host startup must not run for enrollment."
            }

        let result = CacheProcessCommand.execute effects (Array.append enrollmentArguments [| "--unexpected" |])

        Assert.That(result.ExitCode, Is.EqualTo(1))

    /// Verifies secret-bearing runtime failures are replaced with a stable redacted process result.
    [<Test>]
    member _.RuntimeFailureIsRedacted() =
        let effects: CacheProcessEffects =
            {
                Enroll = fun _ -> Error "server rejected token super-secret"
                Status = fun () -> failwith "Status must not run for enrollment."
                Run = fun () -> failwith "Host startup must not run for enrollment."
            }

        let result = CacheProcessCommand.execute effects enrollmentArguments

        Assert.That(result.ExitCode, Is.EqualTo(1))
        Assert.That(result.Payload, Does.Not.Contain("super-secret"))
        Assert.That(result.Payload, Does.Contain("Cache enrollment failed."))
        Assert.That(result.Payload, Does.Contain("Administrator inspection or revocation"))

    /// Verifies status output uses Grace's F# serializer so optional values have the stable string-or-null JSON shape on every platform.
    [<Test>]
    member _.SuccessfulStatusUsesStringOrNullJsonFields() =
        let effects: CacheProcessEffects =
            {
                Enroll = fun _ -> failwith "Enrollment must not run for status."
                Status = fun () -> Ok(CacheRuntimeStatus.registered (Guid.Parse "44444444-4444-4444-4444-444444444444") "https")
                Run = fun () -> failwith "Host startup must not run for status."
            }

        let result = CacheProcessCommand.execute effects [| "--status" |]

        use document = JsonDocument.Parse(result.Payload)
        let status = document.RootElement
        Assert.That(status.GetProperty("Lifecycle").GetString(), Is.EqualTo("registered"))
        Assert.That(status.GetProperty("CacheId").GetString(), Is.EqualTo("44444444-4444-4444-4444-444444444444"))
        Assert.That(status.GetProperty("Transport").GetString(), Is.EqualTo("https"))

    /// Verifies a terminal persisted lifecycle remains a successful machine-readable status observation.
    [<Test>]
    member _.OperatorRecoveryRequiredStatusUsesStableJsonAndSuccessExit() =
        let cacheId = Guid.Parse "44444444-4444-4444-4444-444444444444"

        let effects: CacheProcessEffects =
            {
                Enroll = fun _ -> failwith "Enrollment must not run for status."
                Status = fun () -> Ok(CacheRuntimeStatus.operatorRecoveryRequired cacheId (Some "https"))
                Run = fun () -> failwith "Host startup must not run for status."
            }

        let result = CacheProcessCommand.execute effects [| "--status" |]

        use document = JsonDocument.Parse(result.Payload)
        Assert.That(result.ExitCode, Is.EqualTo(0))

        Assert.That(
            document
                .RootElement
                .GetProperty("Lifecycle")
                .GetString(),
            Is.EqualTo("operator-recovery-required")
        )

        Assert.That(
            document
                .RootElement
                .GetProperty("CacheId")
                .GetString(),
            Is.EqualTo(cacheId.ToString("D"))
        )

    /// Verifies every one-shot process verb rejects trailing input before reaching a runtime or local-control effect.
    [<TestCase("--run")>]
    [<TestCase("--status")>]
    member _.OneShotVerbsRejectTrailingTokensBeforeEffects(marker) =
        let mutable calls = 0

        let effects: CacheProcessEffects =
            {
                Enroll = fun _ -> failwith "Enrollment must not run."
                Status =
                    fun () ->
                        calls <- calls + 1
                        Ok(CacheRuntimeStatus.registered Guid.Empty "https")
                Run =
                    fun () ->
                        calls <- calls + 1
                        Ok(CacheRuntimeStatus.registered Guid.Empty "https")
            }

        let result = CacheProcessCommand.execute effects [| marker; "--unsupported" |]

        Assert.That(result.ExitCode, Is.EqualTo(1))
        Assert.That(calls, Is.EqualTo(0))

    /// Verifies malformed and wrong-envelope successful bodies become stable rejected results before a runtime boundary can surface them.
    [<TestCase("{")>]
    [<TestCase("{\"ReturnValue\": null}")>]
    [<TestCase("{\"unexpected\": true}")>]
    member _.MalformedSuccessfulServerBodyIsRejected(body) =
        CacheServerResponse.tryReadRegistrationResult body
        |> Result.isError
        |> Assert.That

    /// Verifies the documented GraceError property pair survives JSON and is the sole candidate response eligible for a fresh-proof retry.
    [<Test>]
    member _.CandidateProofStaleMarkerRequiresTheExactStructuredGraceErrorPair() =
        let marked = CacheRegistrationProof.candidateProofTimestampStaleError "correlation"

        let markedBody = JsonSerializer.Serialize(marked, Constants.JsonSerializerOptions)
        let missingBody = JsonSerializer.Serialize(Grace.Types.Common.GraceError.Create "ignored" "correlation", Constants.JsonSerializerOptions)
        let unknown = Grace.Types.Common.GraceError.Create "ignored" "correlation"

        unknown.enhance (CacheRegistrationProof.CandidateProofTimestampStalePropertyKey, "unknown")
        |> ignore

        let unknownBody = JsonSerializer.Serialize(unknown, Constants.JsonSerializerOptions)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(CacheServerResponse.isRetryableCandidateProofTimestampStale markedBody, Is.True)
                Assert.That(CacheServerResponse.isRetryableCandidateProofTimestampStale missingBody, Is.False)
                Assert.That(CacheServerResponse.isRetryableCandidateProofTimestampStale unknownBody, Is.False)
                Assert.That(CacheServerResponse.isRetryableCandidateProofTimestampStale "{", Is.False))
        )

    /// Verifies route composition retains the configured Grace Server path base while cache endpoints remain independent origins.
    [<Test>]
    member _.ServerRoutePreservesPathBase() =
        let route = CacheServerRoute.append (Uri("https://gateway.example/grace")) "cache/refresh"

        Assert.That(route.AbsoluteUri, Is.EqualTo("https://gateway.example/grace/cache/refresh"))

    /// Verifies explicit false and stray positional input after the marker-only HTTP exception cannot reach enrollment effects.
    [<Test>]
    member _.AllowHttpRejectsExplicitFalseAndStrayTokensBeforeEnrollment() =
        let mutable calls = 0

        let effects: CacheProcessEffects =
            {
                Enroll =
                    fun _ ->
                        calls <- calls + 1
                        failwith "Malformed marker-only input must not enroll."
                Status = fun () -> failwith "Status must not run for enrollment."
                Run = fun () -> failwith "Host startup must not run for enrollment."
            }

        let httpArguments =
            enrollmentArguments
            |> Array.map (fun argument ->
                if argument = "https://cache.example.test" then
                    "http://cache.example.test"
                else
                    argument)

        let explicitFalse = CacheProcessCommand.execute effects (Array.append httpArguments [| "--allow-http"; "false" |])
        let strayToken = CacheProcessCommand.execute effects (Array.append httpArguments [| "stray" |])

        let validMarker =
            CacheProcessCommand.execute
                { effects with Enroll = fun _ -> Ok(CacheRuntimeStatus.registered Guid.Empty "http-approved") }
                (Array.append httpArguments [| "--allow-http" |])

        Assert.That(explicitFalse.ExitCode, Is.EqualTo(1))
        Assert.That(strayToken.ExitCode, Is.EqualTo(1))
        Assert.That(validMarker.ExitCode, Is.EqualTo(0))
        Assert.That(calls, Is.EqualTo(0))

    /// Verifies cache control retries only explicitly transient server failures and retains no retry workflow state.
    [<Test>]
    member _.TransientServerFailuresUseExactlyThreeAttempts() =
        let mutable calls = 0
        let pauses = ResizeArray<int>()

        let result =
            CacheRetry.execute
                (fun () ->
                    calls <- calls + 1
                    Error true)
                pauses.Add

        Assert.That(result, Is.EqualTo(Error true))
        Assert.That(calls, Is.EqualTo(3))
        Assert.That(pauses, Is.EquivalentTo([ 1; 2 ]))

    /// Verifies a non-transient server rejection never retries or turns into pending local work.
    [<Test>]
    member _.NonTransientServerFailureDoesNotRetry() =
        let mutable calls = 0

        let result =
            CacheRetry.execute
                (fun () ->
                    calls <- calls + 1
                    Error false)
                ignore

        Assert.That(result, Is.EqualTo(Error false))
        Assert.That(calls, Is.EqualTo(1))

    /// Verifies missing or corrupt current-key references fail before replacement creation can orphan a certificate.
    [<Test>]
    member _.RotationNeverCreatesReplacementWhenCurrentKeyCannotOpen() =
        let mutable replacementsCreated = 0

        let result =
            KeyRotationPreparation.openCurrentBeforeCreatingReplacement
                (fun () -> Error "current key is missing or corrupt")
                (fun () ->
                    replacementsCreated <- replacementsCreated + 1
                    Ok "replacement")

        match result with
        | Ok _ -> Assert.Fail("A missing or corrupt current key must prevent replacement creation.")
        | Error error -> Assert.That(error, Is.EqualTo("current key is missing or corrupt"))

        Assert.That(replacementsCreated, Is.EqualTo(0))

    /// Verifies enrollment retries only failures proven before dispatch and never repeats an ambiguous post-dispatch outcome.
    [<Test>]
    member _.EnrollmentRetryStopsAfterOneAmbiguousDispatch() =
        let mutable preSendCalls = 0
        let mutable ambiguousCalls = 0

        let preSendResult =
            EnrollmentRetry.execute
                (fun () ->
                    preSendCalls <- preSendCalls + 1
                    PreSendFailure)
                ignore

        let ambiguousResult =
            EnrollmentRetry.execute
                (fun () ->
                    ambiguousCalls <- ambiguousCalls + 1
                    MayHaveReachedServer)
                ignore

        Assert.That(preSendResult, Is.EqualTo(PreSendFailure))
        Assert.That(preSendCalls, Is.EqualTo(3))
        Assert.That(ambiguousResult, Is.EqualTo(MayHaveReachedServer))
        Assert.That(ambiguousCalls, Is.EqualTo(1))

    /// Verifies rate-limited candidate submission remains pending while only non-rate-limited 4xx responses are definitive rejections.
    [<Test>]
    member _.CandidateRateLimitIsNotDefiniteRejection() =
        Assert.That(CacheHttpFailure.isDefiniteContractRejection 400, Is.True)
        Assert.That(CacheHttpFailure.isDefiniteContractRejection 499, Is.True)
        Assert.That(CacheHttpFailure.isDefiniteContractRejection 429, Is.False)
        Assert.That(CacheHttpFailure.isDefiniteContractRejection 500, Is.False)
        Assert.That(CacheHttpFailure.isDefiniteContractRejection 503, Is.False)

    /// Verifies refresh treats rate limiting as transient while enrollment and candidate retain their existing ambiguity rules.
    [<Test>]
    member _.RefreshRateLimitUsesTheExistingRetryClassification() =
        Assert.That(CacheRefreshHttpFailure.isRetryable 429, Is.True)
        Assert.That(CacheRefreshHttpFailure.isRetryable 500, Is.True)
        Assert.That(CacheRefreshHttpFailure.isRetryable 400, Is.False)
        Assert.That(CacheHttpFailure.isDefiniteContractRejection 429, Is.False)

    /// Verifies rotation retries only pre-send failures and never resends after a transport outcome that may follow acceptance.
    [<Test>]
    member _.RotationRetryStopsAfterOneAmbiguousDispatch() =
        let mutable preSendCalls = 0
        let mutable ambiguousCalls = 0

        let preSendResult =
            RotationRetry.execute
                (fun () ->
                    preSendCalls <- preSendCalls + 1
                    RotationPreSendFailure)
                ignore

        let ambiguousResult =
            RotationRetry.execute
                (fun () ->
                    ambiguousCalls <- ambiguousCalls + 1
                    RotationMayHaveReachedServer)
                ignore

        Assert.That(preSendResult, Is.EqualTo(RotationPreSendFailure))
        Assert.That(preSendCalls, Is.EqualTo(3))
        Assert.That(ambiguousResult, Is.EqualTo(RotationMayHaveReachedServer))
        Assert.That(ambiguousCalls, Is.EqualTo(1))

    /// Verifies only the documented stale-proof response rebuilds and resends the same candidate request with a fresh proof.
    [<Test>]
    member _.CandidateProofStaleRetryBuildsExactlyOneFreshRequest() =
        let built = ResizeArray<int>()
        let mutable nextRequest = 0

        let result =
            RotationRetry.executeWithRequest
                (fun () ->
                    nextRequest <- nextRequest + 1
                    nextRequest)
                (fun request ->
                    built.Add request

                    if request = 1 then
                        RotationCandidateProofTimestampStale
                    else
                        RotationCompleted "accepted")
                ignore

        Assert.That(result, Is.EqualTo(RotationCompleted "accepted"))
        Assert.That(built.ToArray() = [| 1; 2 |], Is.True)

    /// Verifies missing, malformed, unknown, and other HTTP 400 candidate failures remain terminal instead of entering the stale-proof retry path.
    [<Test>]
    member _.CandidateProofRetryDoesNotRetryTerminalResponses() =
        let mutable calls = 0

        let result =
            RotationRetry.executeWithRequest
                (fun () ->
                    calls <- calls + 1
                    calls)
                (fun _ -> RotationRejectedByServer)
                ignore

        Assert.That(result, Is.EqualTo(RotationRejectedByServer))
        Assert.That(calls, Is.EqualTo(1))

    /// Verifies retryable refresh transport failures rebuild the request so later proof attempts have a new observation value.
    [<Test>]
    member _.RefreshRetryBuildsFreshRequestForEveryAttempt() =
        let built = ResizeArray<int>()
        let mutable nextRequest = 0

        let result =
            CachePostRetry.executeWithRequest
                (fun () ->
                    nextRequest <- nextRequest + 1
                    nextRequest)
                (fun request ->
                    built.Add request

                    if request = 1 then Error Retryable else Ok "refreshed")
                ignore

        Assert.That(result, Is.EqualTo(Ok "refreshed": Result<string, CachePostFailure>))
        Assert.That(built.ToArray() = [| 1; 2 |], Is.True)

    /// Verifies a malformed successful rotation result is ambiguous and cannot be retried or treated as a definite rejection.
    [<Test>]
    member _.AmbiguousPostResultRetainsRecoveryDecisionWithoutRetry() =
        let mutable calls = 0

        let result =
            CachePostRetry.execute
                (fun () ->
                    calls <- calls + 1
                    Error Ambiguous)
                ignore

        Assert.That(result, Is.EqualTo(Error Ambiguous))
        Assert.That(calls, Is.EqualTo(1))

    /// Verifies the portable ES256 helper emits only the public #600 DTO shape needed by cache enrollment and rotation.
    [<Test>]
    member _.PortableEs256PublicKeyUsesTheApprovedContract() =
        use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let parameters = key.ExportParameters(false)

        let publicKey = CacheIdentityPublicKey.Create(Base64Url.encode parameters.Q.X, Base64Url.encode parameters.Q.Y)

        Assert.That(CacheRegistrationProof.isValidPublicKey publicKey, Is.True)
