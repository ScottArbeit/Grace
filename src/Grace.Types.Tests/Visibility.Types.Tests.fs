namespace Grace.Types.Tests

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Shared.Parameters.Branch
open Grace.Shared.Parameters.PromotionSet
open Grace.Shared.Parameters.Visibility
open Grace.Types.Branch
open Grace.Types.Common
open Grace.Types.PromotionSet
open Grace.Types.Reference
open Grace.Types.Visibility
open MessagePack
open NUnit.Framework
open System

/// Contains tests covering shared visibility and ownership contract behavior.
[<Parallelizable(ParallelScope.All)>]
type VisibilityContractTests() =

    /// Verifies that branch, reference, and promotion-set defaults expose the implemented public repository-owned scaffold.
    [<Test>]
    member _.DefaultDtosUsePublicRepositoryOwnedScaffoldValues() =
        Assert.That(BranchDto.Default.Visibility, Is.EqualTo(ResourceVisibility.Public))
        Assert.That(BranchDto.Default.Ownership, Is.EqualTo(ResourceOwnership.RepositoryOwned))
        Assert.That(ReferenceDto.Default.Visibility, Is.EqualTo(ResourceVisibility.Public))
        Assert.That(ReferenceDto.Default.Ownership, Is.EqualTo(ResourceOwnership.RepositoryOwned))
        Assert.That(PromotionSetDto.Default.Visibility, Is.EqualTo(ResourceVisibility.Public))
        Assert.That(PromotionSetDto.Default.Ownership, Is.EqualTo(ResourceOwnership.RepositoryOwned))

    /// Verifies that content-object contracts do not gain global visibility or ownership fields.
    [<Test>]
    member _.ContentObjectContractsDoNotExposeGlobalVisibilityOrOwnershipFields() =
        Assert.That(typeof<FileVersion>.GetProperty ("Visibility"), Is.Null)
        Assert.That(typeof<FileVersion>.GetProperty ("Ownership"), Is.Null)
        Assert.That(typeof<DirectoryVersion>.GetProperty ("Visibility"), Is.Null)
        Assert.That(typeof<DirectoryVersion>.GetProperty ("Ownership"), Is.Null)

    /// Verifies that current create endpoints do not expose no-op visibility inputs before route behavior exists.
    [<Test>]
    member _.CurrentCreateParametersDoNotExposeVisibilityOrOwnershipFields() =
        Assert.That(typeof<CreateBranchParameters>.GetProperty ("Visibility"), Is.Null)
        Assert.That(typeof<CreateBranchParameters>.GetProperty ("Ownership"), Is.Null)
        Assert.That(typeof<CreatePromotionSetParameters>.GetProperty ("Visibility"), Is.Null)
        Assert.That(typeof<CreatePromotionSetParameters>.GetProperty ("Ownership"), Is.Null)

    /// Verifies that supported visibility and ownership values round-trip through JSON field serialization.
    [<Test>]
    member _.SupportedDtoFieldsRoundTripThroughJson() =
        let branchDto = { BranchDto.Default with Visibility = ResourceVisibility.Private; Ownership = ResourceOwnership.ContributorOwned }

        let referenceDto = { ReferenceDto.Default with Visibility = ResourceVisibility.Private; Ownership = ResourceOwnership.ContributorOwned }

        let promotionSetDto = { PromotionSetDto.Default with Visibility = ResourceVisibility.Private; Ownership = ResourceOwnership.ContributorOwned }

        let branchJsonRoundTrip = deserialize<BranchDto> (serialize branchDto)
        let referenceJsonRoundTrip = deserialize<ReferenceDto> (serialize referenceDto)
        let promotionSetJsonRoundTrip = deserialize<PromotionSetDto> (serialize promotionSetDto)

        Assert.That(branchJsonRoundTrip.Visibility, Is.EqualTo(ResourceVisibility.Private))
        Assert.That(branchJsonRoundTrip.Ownership, Is.EqualTo(ResourceOwnership.ContributorOwned))
        Assert.That(referenceJsonRoundTrip.Visibility, Is.EqualTo(ResourceVisibility.Private))
        Assert.That(referenceJsonRoundTrip.Ownership, Is.EqualTo(ResourceOwnership.ContributorOwned))
        Assert.That(promotionSetJsonRoundTrip.Visibility, Is.EqualTo(ResourceVisibility.Private))
        Assert.That(promotionSetJsonRoundTrip.Ownership, Is.EqualTo(ResourceOwnership.ContributorOwned))

    /// Verifies that shared visibility and ownership values round-trip through MessagePack.
    [<Test>]
    member _.SupportedValuesRoundTripThroughMessagePack() =
        let visibilityBytes = MessagePackSerializer.Serialize(ResourceVisibility.Private, messagePackSerializerOptions)
        let ownershipBytes = MessagePackSerializer.Serialize(ResourceOwnership.ContributorOwned, messagePackSerializerOptions)

        let visibilityRoundTrip = MessagePackSerializer.Deserialize<ResourceVisibility>(visibilityBytes, messagePackSerializerOptions)
        let ownershipRoundTrip = MessagePackSerializer.Deserialize<ResourceOwnership>(ownershipBytes, messagePackSerializerOptions)

        Assert.That(visibilityRoundTrip, Is.EqualTo(ResourceVisibility.Private))
        Assert.That(ownershipRoundTrip, Is.EqualTo(ResourceOwnership.ContributorOwned))

    /// Verifies that public visibility parsing accepts only implemented values.
    [<Test>]
    member _.VisibilityPublicParserAcceptsImplementedValues() =
        let cases =
            [|
                "Public", ResourceVisibility.Public
                "public", ResourceVisibility.Public
                "Private", ResourceVisibility.Private
                "private", ResourceVisibility.Private
            |]

        for input, expected in cases do
            Assert.That(ResourceVisibility.TryParsePublicInput input, Is.EqualTo(Some expected), input)

    /// Verifies that public ownership parsing accepts only implemented values.
    [<Test>]
    member _.OwnershipPublicParserAcceptsImplementedValues() =
        let cases =
            [|
                "RepositoryOwned", ResourceOwnership.RepositoryOwned
                "repositoryowned", ResourceOwnership.RepositoryOwned
                "ContributorOwned", ResourceOwnership.ContributorOwned
                "contributorowned", ResourceOwnership.ContributorOwned
            |]

        for input, expected in cases do
            Assert.That(ResourceOwnership.TryParsePublicInput input, Is.EqualTo(Some expected), input)

    /// Verifies that deferred or arbitrary visibility values are rejected before they become contract values.
    [<TestCase("SecurityEmbargoed")>]
    [<TestCase("Internal")>]
    [<TestCase("")>]
    [<TestCase(null)>]
    member _.VisibilityPublicParserRejectsDeferredAndUnknownValues(input: string) =
        Assert.That(
            (ResourceVisibility.TryParsePublicInput input)
                .IsNone,
            Is.True
        )

    /// Verifies that ownership parsing rejects arbitrary contributor owner identifiers.
    [<TestCase("OwnerId:11111111-1111-1111-1111-111111111111")>]
    [<TestCase("alice@example.test")>]
    [<TestCase("TeamOwned")>]
    [<TestCase("")>]
    [<TestCase(null)>]
    member _.OwnershipPublicParserRejectsArbitraryContributorOwnerValues(input: string) =
        Assert.That(
            (ResourceOwnership.TryParsePublicInput input)
                .IsNone,
            Is.True
        )

    /// Verifies that shared parameters reject deferred public visibility input.
    [<Test>]
    member _.SharedParametersRejectDeferredVisibilityAtPublicBoundary() =
        let parameters = VisibilityOwnershipParameters()
        parameters.Visibility <- "SecurityEmbargoed"
        parameters.Ownership <- "ContributorOwned"

        Assert.That(parameters.TryParseVisibility().IsNone, Is.True)
        Assert.That(parameters.TryParseOwnership(), Is.EqualTo(Some ResourceOwnership.ContributorOwned))

    /// Verifies that shared parameters reject arbitrary ownership at the public boundary.
    [<Test>]
    member _.SharedParametersRejectArbitraryOwnershipAtPublicBoundary() =
        let parameters = VisibilityOwnershipParameters()
        parameters.Visibility <- "Private"
        parameters.Ownership <- "OwnerId:11111111-1111-1111-1111-111111111111"

        Assert.That(parameters.TryParseVisibility(), Is.EqualTo(Some ResourceVisibility.Private))
        Assert.That(parameters.TryParseOwnership().IsNone, Is.True)
