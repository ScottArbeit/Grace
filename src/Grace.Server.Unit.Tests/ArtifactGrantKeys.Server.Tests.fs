namespace Grace.Server.Tests

open Grace.Server.Security.ArtifactGrantKeys
open Grace.Shared.ArtifactGrant
open Grace.Types.ArtifactGrant
open Grace.Types.Common
open Grace.Types.MaterializationPlan
open Microsoft.Extensions.Logging.Abstractions
open NodaTime
open NUnit.Framework
open System

/// Covers server-owned artifact grant signing keys and issuance behavior.
[<Parallelizable(ParallelScope.All)>]
type ArtifactGrantKeysServerTests() =

    let now = Instant.FromUtc(2026, 7, 9, 12, 0)
    let cacheServicePrincipalId = "cache-service-client"
    let targetRoot = DirectoryVersionId.Parse "11111111-1111-1111-1111-111111111111"
    let artifactIdentity = "GraceZipFiles/11111111-1111-1111-1111-111111111111.zip"

    /// Builds an artifact grant key ring with a no-op logger.
    let keyRing () = ArtifactGrantKeyRing(NullLogger<ArtifactGrantKeyRing>.Instance)

    /// Builds a cache-mode grant issuance request.
    let issueRequest ttl =
        {
            CacheServicePrincipalId = cacheServicePrincipalId
            TargetRootDirectoryVersionId = targetRoot
            ExecutionMode = MaterializationExecutionMode.CacheRequired
            ArtifactIdentities = [ artifactIdentity ]
            RequestedTtl = ttl
        }

    /// Builds the validation request that should match the issued grant.
    let validationRequest () =
        ArtifactGrantValidationRequest.Create(cacheServicePrincipalId, targetRoot, MaterializationExecutionMode.CacheRequired, artifactIdentity)

    /// Asserts that local validation accepted an issued server grant.
    let assertValidationOk result =
        match result with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected issued grant to validate, got {ArtifactGrantValidationError.toMessage error}.")

    /// Asserts that grant issuance failed with the expected non-secret reason.
    let assertIssueError expected result =
        match result with
        | Ok _ -> Assert.Fail("Expected grant issuance to fail.")
        | Error actual -> if actual <> expected then Assert.Fail($"Expected {expected}, got {actual}.")

    [<Test>]
    member _.``issuer signs grants with five minute default ttl and explicit artifact binding``() =
        let ring = keyRing ()

        match ring.IssueGrant(now, issueRequest None) with
        | Error error -> Assert.Fail(ArtifactGrantIssueError.toMessage error)
        | Ok grant ->
            Assert.That(grant.Payload.ExpiresAt, Is.EqualTo(now.Plus(ArtifactGrantContract.DefaultGrantTtl)))
            Assert.That(grant.Payload.ArtifactIdentities, Is.EquivalentTo([ artifactIdentity ]))

            let keys = ring.PublishValidationKeys now
            Assert.That(keys.CacheTtl, Is.EqualTo ArtifactGrantContract.ValidationKeyCacheTtl)

            validateWithKeySet now keys (validationRequest ()) grant
            |> assertValidationOk

    [<Test>]
    member _.``issuer rejects broad missing artifact bindings and overlong ttl``() =
        let ring = keyRing ()

        let noArtifacts = { issueRequest None with ArtifactIdentities = [] }

        let overlong =
            { issueRequest (Some(ArtifactGrantContract.MaximumAcceptedGrantTtl.Plus(Duration.FromTicks 1L))) with ArtifactIdentities = [ artifactIdentity ] }

        ring.IssueGrant(now, noArtifacts)
        |> assertIssueError InvalidArtifactIdentities

        ring.IssueGrant(now, overlong)
        |> assertIssueError RequestedTtlTooLong

    [<Test>]
    member _.``issuer does not require artifact grants for direct mode``() =
        let ring = keyRing ()

        let direct = { issueRequest None with ExecutionMode = MaterializationExecutionMode.Direct }

        ring.IssueGrant(now, direct)
        |> assertIssueError InvalidExecutionMode

    [<Test>]
    member _.``signing key rotates after two hours and publishes overlap key until grant expiry window ends``() =
        let ring = keyRing ()

        let firstGrant =
            match ring.IssueGrant(now, issueRequest None) with
            | Error error -> failwith (ArtifactGrantIssueError.toMessage error)
            | Ok grant -> grant

        let rotatedAt =
            now
                .Plus(ArtifactGrantContract.SigningKeyActiveLifetime)
                .Plus(Duration.FromTicks 1L)

        let secondGrant =
            match ring.IssueGrant(rotatedAt, issueRequest None) with
            | Error error -> failwith (ArtifactGrantIssueError.toMessage error)
            | Ok grant -> grant

        Assert.That(secondGrant.Header.KeyId, Is.Not.EqualTo firstGrant.Header.KeyId)

        let overlapKeys = ring.PublishValidationKeys rotatedAt
        Assert.That(overlapKeys.Keys |> Seq.map (fun key -> key.KeyId), Does.Contain firstGrant.Header.KeyId)
        Assert.That(overlapKeys.Keys |> Seq.map (fun key -> key.KeyId), Does.Contain secondGrant.Header.KeyId)

        let afterOverlap =
            now
                .Plus(ArtifactGrantContract.SigningKeyActiveLifetime)
                .Plus(ArtifactGrantContract.MaximumAcceptedGrantTtl)
                .Plus(Duration.FromTicks 1L)

        let postOverlapKeys = ring.PublishValidationKeys afterOverlap

        Assert.That(
            postOverlapKeys.Keys
            |> Seq.map (fun key -> key.KeyId),
            Does.Not.Contain firstGrant.Header.KeyId
        )

        Assert.That(
            postOverlapKeys.Keys
            |> Seq.map (fun key -> key.KeyId),
            Does.Contain secondGrant.Header.KeyId
        )

    [<Test>]
    member _.``validation-key publication rotates before issuing next grant after active window``() =
        let ring = keyRing ()

        let firstGrant =
            match ring.IssueGrant(now, issueRequest None) with
            | Error error -> failwith (ArtifactGrantIssueError.toMessage error)
            | Ok grant -> grant

        let rotatedAt =
            now
                .Plus(ArtifactGrantContract.SigningKeyActiveLifetime)
                .Plus(Duration.FromTicks 1L)

        let publishedBeforeIssue = ring.PublishValidationKeys rotatedAt

        let secondGrant =
            match ring.IssueGrant(rotatedAt, issueRequest None) with
            | Error error -> failwith (ArtifactGrantIssueError.toMessage error)
            | Ok grant -> grant

        Assert.That(secondGrant.Header.KeyId, Is.Not.EqualTo firstGrant.Header.KeyId)

        Assert.That(
            publishedBeforeIssue.Keys
            |> Seq.map (fun key -> key.KeyId),
            Does.Contain secondGrant.Header.KeyId
        )

        validateWithKeySet rotatedAt publishedBeforeIssue (validationRequest ()) secondGrant
        |> assertValidationOk
