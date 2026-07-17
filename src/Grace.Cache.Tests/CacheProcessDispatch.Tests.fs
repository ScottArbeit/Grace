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
                RotateNow = fun () -> failwith "Rotation must not run for enrollment."
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
                RotateNow = fun () -> failwith "Rotation must not run for enrollment."
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

    /// Verifies unrecognized process options cannot silently broaden enrollment input before any runtime side effect.
    [<Test>]
    member _.UnknownEnrollmentOptionHasNoRuntimeSideEffects() =
        let effects: CacheProcessEffects =
            {
                Enroll = fun _ -> failwith "Unknown enrollment input must not reach the runtime boundary."
                RotateNow = fun () -> failwith "Rotation must not run for enrollment."
                Status = fun () -> failwith "Status must not run for enrollment."
                Run = fun () -> failwith "Host startup must not run for enrollment."
            }

        let result = CacheProcessCommand.execute effects (Array.append enrollmentArguments [| "--unexpected" |])

        Assert.That(result.ExitCode, Is.EqualTo(1))

    /// Verifies immediate rotation reaches its proof-only runtime boundary without reading enrollment input.
    [<Test>]
    member _.RotationDispatchesToRuntimeBoundary() =
        let mutable rotations = 0

        let effects: CacheProcessEffects =
            {
                Enroll = fun _ -> failwith "Enrollment must not run for rotation."
                RotateNow =
                    fun () ->
                        rotations <- rotations + 1
                        Ok(CacheRuntimeStatus.registered (Guid.Parse "44444444-4444-4444-4444-444444444444") "https")
                Status = fun () -> failwith "Status must not run for rotation."
                Run = fun () -> failwith "Host startup must not run for rotation."
            }

        let result = CacheProcessCommand.execute effects [| "--rotate-now" |]

        Assert.That(result.ExitCode, Is.EqualTo(0))
        Assert.That(rotations, Is.EqualTo(1))
        Assert.That(result.Payload, Does.Not.Contain("private"))

    /// Verifies secret-bearing runtime failures are replaced with a stable redacted process result.
    [<Test>]
    member _.RuntimeFailureIsRedacted() =
        let effects: CacheProcessEffects =
            {
                Enroll = fun _ -> Error "server rejected token super-secret"
                RotateNow = fun () -> failwith "Rotation must not run for enrollment."
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
                RotateNow = fun () -> failwith "Rotation must not run for status."
                Status = fun () -> Ok(CacheRuntimeStatus.registered (Guid.Parse "44444444-4444-4444-4444-444444444444") "https")
                Run = fun () -> failwith "Host startup must not run for status."
            }

        let result = CacheProcessCommand.execute effects [| "--status" |]

        use document = JsonDocument.Parse(result.Payload)
        let status = document.RootElement
        Assert.That(status.GetProperty("Lifecycle").GetString(), Is.EqualTo("registered"))
        Assert.That(status.GetProperty("CacheId").GetString(), Is.EqualTo("44444444-4444-4444-4444-444444444444"))
        Assert.That(status.GetProperty("Transport").GetString(), Is.EqualTo("https"))

    /// Verifies every one-shot process verb rejects trailing input before reaching a runtime or local-control effect.
    [<TestCase("--run")>]
    [<TestCase("--status")>]
    [<TestCase("--rotate-now")>]
    member _.OneShotVerbsRejectTrailingTokensBeforeEffects(marker) =
        let mutable calls = 0

        let effects: CacheProcessEffects =
            {
                Enroll = fun _ -> failwith "Enrollment must not run."
                RotateNow =
                    fun () ->
                        calls <- calls + 1
                        Ok(CacheRuntimeStatus.registered Guid.Empty "https")
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
                RotateNow = fun () -> failwith "Rotation must not run for enrollment."
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

    /// Verifies only a 4xx response is a definite cache contract rejection while post-dispatch 5xx outcomes remain unknown.
    [<Test>]
    member _.PostDispatchEnrollmentFiveXxIsNotDefiniteRejection() =
        Assert.That(CacheHttpFailure.isDefiniteContractRejection 400, Is.True)
        Assert.That(CacheHttpFailure.isDefiniteContractRejection 499, Is.True)
        Assert.That(CacheHttpFailure.isDefiniteContractRejection 500, Is.False)
        Assert.That(CacheHttpFailure.isDefiniteContractRejection 503, Is.False)

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

    /// Verifies an absent or refused Unix control socket becomes the stable redacted local-control rejection.
    [<Test>]
    member _.UnixControlSocketFailureIsRedacted() =
        let result = CacheLocalControl.requestRotationWith (fun () -> raise (SocketException()))

        match result with
        | Ok _ -> Assert.Fail("An unavailable Unix control socket must not return a cache status.")
        | Error error -> Assert.That(error, Is.EqualTo("Grace Cache rotation request was not accepted."))

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
