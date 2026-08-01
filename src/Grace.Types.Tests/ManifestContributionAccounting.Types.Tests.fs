namespace Grace.Types.Tests

open Grace.Types.Common
open Grace.Types.ManifestContributionAccounting
open Microsoft.FSharp.Reflection
open NUnit.Framework
open System
open System.Reflection

/// Covers deterministic exact-relationship identities and explicit read bounds.
[<Parallelizable(ParallelScope.All)>]
type ManifestContributionAccountingTypesTests() =
    let repositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let rootDirectoryVersionId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    let childDirectoryVersionId = Guid.Parse("33333333-3333-3333-3333-333333333333")
    let parentDirectoryVersionId = Guid.Parse("44444444-4444-4444-4444-444444444444")
    let referenceId = Guid.Parse("55555555-5555-5555-5555-555555555555")

    /// Unwraps a successful contract result or fails the active test with the validation error.
    let expectOk result =
        match result with
        | Ok value -> value
        | Error error ->
            Assert.Fail(error)
            Unchecked.defaultof<_>

    /// Requires a contract validation result to reject its input.
    let assertError result =
        match result with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Expected contract validation to reject the input.")

    /// Requires a Result-returning boundary to reject invalid input without leaking a runtime exception.
    let assertRejectedWithoutThrowing operation =
        try
            operation () |> assertError
        with
        | ex -> Assert.Fail($"Expected contract validation to return Error, but it threw {ex.GetType().Name}: {ex.Message}")

    /// Materializes a private union payload so defensive unwrapping can be tested independently of its creator.
    let materializeReadBound maximumCount =
        let unionCase =
            FSharpType.GetUnionCases(typeof<ExactRelationshipReadBound>, BindingFlags.NonPublic)
            |> Array.exactlyOne

        FSharpValue.MakeUnion(unionCase, [| box maximumCount |], BindingFlags.NonPublic) :?> ExactRelationshipReadBound

    /// Verifies that a relationship survives its deterministic storage-key round trip.
    let assertRoundTrip relationship =
        let key =
            ExactRelationshipKey.create relationship
            |> expectOk

        let partitionKey =
            relationship
            |> ExactRelationshipKey.partition
            |> expectOk
            |> ExactRelationshipKey.createPartitionKey
            |> expectOk

        let parsed = ExactRelationshipKey.tryParse key |> expectOk

        Assert.That(partitionKey, Is.EqualTo(key.PartitionKey))
        Assert.That(parsed, Is.EqualTo(relationship))

    /// Verifies deterministic round trips for each exact relationship kind.
    [<Test>]
    member _.EveryRelationshipKindRoundTripsThroughItsStorageKey() =
        let relationships =
            [|
                ExactRelationship.ReferenceRoot { RepositoryId = repositoryId; RootDirectoryVersionId = rootDirectoryVersionId; ReferenceId = referenceId }
                ExactRelationship.ParentChild
                    { RepositoryId = repositoryId; ParentDirectoryVersionId = parentDirectoryVersionId; ChildDirectoryVersionId = childDirectoryVersionId }
                ExactRelationship.DirectoryVersionManifest
                    {
                        RepositoryId = repositoryId
                        StoragePoolId = "pool|primary:/A"
                        ManifestAddress = "manifest:blake3/a|b?c"
                        DirectoryVersionId = rootDirectoryVersionId
                    }
            |]

        relationships |> Array.iter assertRoundTrip

    /// Verifies bounded enumeration and relationship writes use the same canonical partition identity.
    [<Test>]
    member _.EnumerationPartitionsMatchRelationshipStorageKeys() =
        let relationships =
            [|
                ExactRelationship.ReferenceRoot { RepositoryId = repositoryId; RootDirectoryVersionId = rootDirectoryVersionId; ReferenceId = referenceId }
                ExactRelationship.ParentChild
                    { RepositoryId = repositoryId; ParentDirectoryVersionId = parentDirectoryVersionId; ChildDirectoryVersionId = childDirectoryVersionId }
                ExactRelationship.DirectoryVersionManifest
                    {
                        RepositoryId = repositoryId
                        StoragePoolId = "pool|primary:/A"
                        ManifestAddress = "manifest:blake3/a|b?c"
                        DirectoryVersionId = rootDirectoryVersionId
                    }
            |]

        relationships
        |> Array.iter (fun relationship ->
            let relationshipKey =
                ExactRelationshipKey.create relationship
                |> expectOk

            let enumerationPartitionKey =
                relationship
                |> ExactRelationshipKey.partition
                |> expectOk
                |> ExactRelationshipKey.createPartitionKey
                |> expectOk

            Assert.That(enumerationPartitionKey, Is.EqualTo(relationshipKey.PartitionKey)))

    /// Verifies invalid partition dimensions cannot produce an enumeration key.
    [<Test>]
    member _.EnumerationPartitionKeysRejectEmptyComponents() =
        [|
            ExactRelationshipPartition.IncomingDirectoryVersion(Guid.Empty, rootDirectoryVersionId)
            ExactRelationshipPartition.IncomingDirectoryVersion(repositoryId, Guid.Empty)
            ExactRelationshipPartition.Manifest(repositoryId, String.Empty, "manifest-address")
            ExactRelationshipPartition.Manifest(repositoryId, "pool", " ")
        |]
        |> Array.iter (
            ExactRelationshipKey.createPartitionKey
            >> assertError
        )

    /// Verifies that relationship kinds and tuple positions cannot collapse to the same key.
    [<Test>]
    member _.RelationshipKindsAndTuplePositionsCannotCollide() =
        let referenceRoot =
            ExactRelationship.ReferenceRoot { RepositoryId = repositoryId; RootDirectoryVersionId = childDirectoryVersionId; ReferenceId = referenceId }

        let parentChild =
            ExactRelationship.ParentChild
                { RepositoryId = repositoryId; ParentDirectoryVersionId = referenceId; ChildDirectoryVersionId = childDirectoryVersionId }

        let reversedParentChild =
            ExactRelationship.ParentChild
                { RepositoryId = repositoryId; ParentDirectoryVersionId = childDirectoryVersionId; ChildDirectoryVersionId = referenceId }

        let keys =
            [|
                referenceRoot
                parentChild
                reversedParentChild
            |]
            |> Array.map (ExactRelationshipKey.create >> expectOk)

        Assert.That(keys |> Array.distinct |> Array.length, Is.EqualTo(keys.Length))

    /// Verifies that delimiter characters and component casing remain distinct and reversible.
    [<Test>]
    member _.StringComponentsPreserveDelimitersAndCasingWithoutCollisions() =
        let relationship (storagePoolId: StoragePoolId) (manifestAddress: ManifestAddress) =
            ExactRelationship.DirectoryVersionManifest
                { RepositoryId = repositoryId; StoragePoolId = storagePoolId; ManifestAddress = manifestAddress; DirectoryVersionId = rootDirectoryVersionId }

        let upper = relationship "Pool|A:/B" "Manifest:ABC/Def|ghi"
        let lower = relationship "pool|a:/b" "manifest:abc/def|ghi"
        let shiftedDelimiter = relationship "Pool" "A:/B|Manifest:ABC/Def|ghi"

        let keys =
            [| upper; lower; shiftedDelimiter |]
            |> Array.map (ExactRelationshipKey.create >> expectOk)

        Assert.That(keys |> Array.distinct |> Array.length, Is.EqualTo(keys.Length))
        assertRoundTrip upper
        assertRoundTrip lower
        assertRoundTrip shiftedDelimiter

    /// Verifies valid Unicode text round trips while distinct ill-formed UTF-16 inputs are rejected before encoding.
    [<Test>]
    member _.UnicodeComponentsRoundTripAndIllFormedUtf16CannotCollide() =
        let relationship (storagePoolId: StoragePoolId) (manifestAddress: ManifestAddress) =
            ExactRelationship.DirectoryVersionManifest
                { RepositoryId = repositoryId; StoragePoolId = storagePoolId; ManifestAddress = manifestAddress; DirectoryVersionId = rootDirectoryVersionId }

        let supplementaryCharacter = Char.ConvertFromUtf32(0x1F680)
        let validUnicode = relationship $"pool-{supplementaryCharacter}" $"manifest-{supplementaryCharacter}"
        let composedUnicode = relationship "pool-\u00E9" "manifest-\u00E9"
        let decomposedUnicode = relationship "pool-e\u0301" "manifest-e\u0301"
        let firstUnpairedSurrogate = String(char 0xD800, 1)
        let secondUnpairedSurrogate = String(char 0xD801, 1)

        let acceptedRelationships =
            [|
                validUnicode
                composedUnicode
                decomposedUnicode
            |]

        acceptedRelationships
        |> Array.iter assertRoundTrip

        let acceptedKeys =
            acceptedRelationships
            |> Array.map (ExactRelationshipKey.create >> expectOk)

        Assert.That(acceptedKeys |> Array.distinct |> Array.length, Is.EqualTo(acceptedKeys.Length))

        [|
            relationship firstUnpairedSurrogate "manifest-address"
            relationship secondUnpairedSurrogate "manifest-address"
            relationship "pool" firstUnpairedSurrogate
            relationship "pool" secondUnpairedSurrogate
        |]
        |> Array.iter (fun malformedRelationship ->
            ExactRelationshipKey.create malformedRelationship
            |> assertError

            ExactRelationshipKey.partition malformedRelationship
            |> assertError)

        [|
            ExactRelationshipPartition.Manifest(repositoryId, firstUnpairedSurrogate, "manifest-address")
            ExactRelationshipPartition.Manifest(repositoryId, "pool", secondUnpairedSurrogate)
        |]
        |> Array.iter (
            ExactRelationshipKey.createPartitionKey
            >> assertError
        )

        assertRejectedWithoutThrowing (fun () ->
            ExactRelationshipKey.tryParse
                {
                    PartitionKey = $"manifest:{repositoryId:N}:{firstUnpairedSurrogate}:cG9vbA"
                    ItemId = $"directory-version-manifest:{rootDirectoryVersionId:N}"
                })

    /// Verifies null public values are rejected by Result-returning key boundaries without throwing.
    [<Test>]
    member _.ResultBoundariesRejectNullPublicValuesWithoutThrowing() =
        let nullRelationship = Unchecked.defaultof<ExactRelationship>
        let nullPartition = Unchecked.defaultof<ExactRelationshipPartition>
        let nullKey = Unchecked.defaultof<ExactRelationshipKey>

        assertRejectedWithoutThrowing (fun () -> ExactRelationshipKey.create nullRelationship)
        assertRejectedWithoutThrowing (fun () -> ExactRelationshipKey.partition nullRelationship)
        assertRejectedWithoutThrowing (fun () -> ExactRelationshipKey.createPartitionKey nullPartition)
        assertRejectedWithoutThrowing (fun () -> ExactRelationshipKey.tryParse nullKey)

    /// Verifies every ExactRelationship case rejects a null record payload without throwing.
    [<Test>]
    member _.RelationshipCasesRejectNullRecordPayloadsWithoutThrowing() =
        [|
            ExactRelationship.ReferenceRoot Unchecked.defaultof<ReferenceRootRelationship>
            ExactRelationship.ParentChild Unchecked.defaultof<ParentChildRelationship>
            ExactRelationship.DirectoryVersionManifest Unchecked.defaultof<DirectoryVersionManifestRelationship>
        |]
        |> Array.iter (fun relationship ->
            assertRejectedWithoutThrowing (fun () -> ExactRelationshipKey.create relationship)
            assertRejectedWithoutThrowing (fun () -> ExactRelationshipKey.partition relationship))

    /// Verifies null string-bearing values from CLR callers are rejected by every relevant boundary.
    [<Test>]
    member _.NullStringBearingValuesAreRejectedWithoutThrowing() =
        let relationships =
            [|
                ExactRelationship.DirectoryVersionManifest
                    { RepositoryId = repositoryId; StoragePoolId = null; ManifestAddress = "manifest-address"; DirectoryVersionId = rootDirectoryVersionId }
                ExactRelationship.DirectoryVersionManifest
                    { RepositoryId = repositoryId; StoragePoolId = "pool"; ManifestAddress = null; DirectoryVersionId = rootDirectoryVersionId }
            |]

        relationships
        |> Array.iter (fun relationship ->
            assertRejectedWithoutThrowing (fun () -> ExactRelationshipKey.create relationship)
            assertRejectedWithoutThrowing (fun () -> ExactRelationshipKey.partition relationship))

        [|
            ExactRelationshipPartition.Manifest(repositoryId, null, "manifest-address")
            ExactRelationshipPartition.Manifest(repositoryId, "pool", null)
        |]
        |> Array.iter (fun partition -> assertRejectedWithoutThrowing (fun () -> ExactRelationshipKey.createPartitionKey partition))

        [|
            { PartitionKey = null; ItemId = $"reference-root:{referenceId:N}" }
            { PartitionKey = $"incoming-directory-version:{repositoryId:N}:{rootDirectoryVersionId:N}"; ItemId = null }
            { PartitionKey = " "; ItemId = $"reference-root:{referenceId:N}" }
            { PartitionKey = $"incoming-directory-version:{repositoryId:N}:{rootDirectoryVersionId:N}"; ItemId = "\t" }
        |]
        |> Array.iter (fun key -> assertRejectedWithoutThrowing (fun () -> ExactRelationshipKey.tryParse key))

    /// Verifies malformed or non-canonical key strings cannot masquerade as exact relationships.
    [<Test>]
    member _.MalformedAndNonCanonicalKeysAreRejected() =
        let canonical =
            ExactRelationship.ReferenceRoot { RepositoryId = repositoryId; RootDirectoryVersionId = rootDirectoryVersionId; ReferenceId = referenceId }
            |> ExactRelationshipKey.create
            |> expectOk

        ExactRelationshipKey.tryParse { canonical with PartitionKey = canonical.PartitionKey.ToUpperInvariant() }
        |> assertError

        ExactRelationshipKey.tryParse
            {
                PartitionKey = $"manifest:{repositoryId:N}:not+base64:{Convert.ToBase64String([| 0uy |])}"
                ItemId = $"directory-version-manifest:{rootDirectoryVersionId:N}"
            }
        |> assertError

        ExactRelationshipKey.tryParse
            { PartitionKey = $"manifest:{repositoryId:N}:_w:cG9vbA"; ItemId = $"directory-version-manifest:{rootDirectoryVersionId:N}" }
        |> assertError

    /// Verifies that empty identity components are rejected before a storage key can exist.
    [<Test>]
    member _.EmptyIdentityComponentsAreRejected() =
        let emptyReference =
            ExactRelationship.ReferenceRoot { RepositoryId = repositoryId; RootDirectoryVersionId = rootDirectoryVersionId; ReferenceId = Guid.Empty }

        let emptyRepository =
            ExactRelationship.ParentChild
                { RepositoryId = Guid.Empty; ParentDirectoryVersionId = parentDirectoryVersionId; ChildDirectoryVersionId = childDirectoryVersionId }

        let emptyStoragePool =
            ExactRelationship.DirectoryVersionManifest
                { RepositoryId = repositoryId; StoragePoolId = " "; ManifestAddress = "manifest-address"; DirectoryVersionId = rootDirectoryVersionId }

        let emptyManifest =
            ExactRelationship.DirectoryVersionManifest
                { RepositoryId = repositoryId; StoragePoolId = "pool"; ManifestAddress = String.Empty; DirectoryVersionId = rootDirectoryVersionId }

        [|
            emptyReference
            emptyRepository
            emptyStoragePool
            emptyManifest
        |]
        |> Array.iter (fun relationship ->
            ExactRelationshipKey.create relationship
            |> assertError

            ExactRelationshipKey.partition relationship
            |> assertError)

    /// Verifies that exact-relationship enumeration always carries a positive finite maximum count.
    [<Test>]
    member _.EnumerationBoundRejectsUnboundedAndOverMaximumRequests() =
        ExactRelationshipReadBound.create 0 |> assertError

        ExactRelationshipReadBound.create -1
        |> assertError

        ExactRelationshipReadBound.create Int32.MinValue
        |> assertError

        ExactRelationshipReadBound.create (ExactRelationshipReadBound.Maximum + 1)
        |> assertError

        ExactRelationshipReadBound.create Int32.MaxValue
        |> assertError

        let minimum = ExactRelationshipReadBound.create 1 |> expectOk

        let maximum =
            ExactRelationshipReadBound.create ExactRelationshipReadBound.Maximum
            |> expectOk

        Assert.That(ExactRelationshipReadBound.value minimum, Is.EqualTo(1))
        Assert.That(ExactRelationshipReadBound.value maximum, Is.EqualTo(ExactRelationshipReadBound.Maximum))

    /// Verifies a language-default value is observably absent and cannot unwrap as an accidental zero bound.
    [<Test>]
    member _.DefaultEnumerationBoundCannotMasqueradeAsZero() =
        let defaultBound = Unchecked.defaultof<ExactRelationshipReadBound>

        Assert.That(box defaultBound, Is.Null)

        Assert.Throws<ArgumentException>(
            Action (fun () ->
                ExactRelationshipReadBound.value defaultBound
                |> ignore)
        )
        |> ignore

    /// Verifies defensive unwrapping cannot expose a representationally invalid finite bound.
    [<Test>]
    member _.InvalidMaterializedEnumerationBoundsCannotBeUnwrapped() =
        [|
            Int32.MinValue
            -1
            0
            ExactRelationshipReadBound.Maximum + 1
            Int32.MaxValue
        |]
        |> Array.iter (fun maximumCount ->
            let invalidBound = materializeReadBound maximumCount

            Assert.Throws<ArgumentException>(
                Action (fun () ->
                    ExactRelationshipReadBound.value invalidBound
                    |> ignore)
            )
            |> ignore)
