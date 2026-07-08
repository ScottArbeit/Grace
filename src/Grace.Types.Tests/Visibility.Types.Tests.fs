namespace Grace.Types.Tests

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Parameters.Branch
open Grace.Shared.Parameters.PromotionSet
open Grace.Shared.Parameters.Visibility
open Grace.Types.Common
open Grace.Types.Visibility
open MessagePack
open Microsoft.FSharp.Reflection
open NUnit.Framework
open System

/// Proves visibility inputs compose with existing branch route parameters without replacing the route base class.
type private BranchCreateVisibilityParameters() =
    inherit CreateBranchParameters()

    /// Accepted visibility input for implemented public surfaces; deferred values such as security embargoes are rejected.
    member val public Visibility = String.Empty with get, set

    /// Accepted ownership input for implemented public surfaces; arbitrary contributor owner identifiers are rejected.
    member val public Ownership = String.Empty with get, set

/// Contains tests covering shared visibility and ownership contract behavior.
[<Parallelizable(ParallelScope.All)>]
type VisibilityContractTests() =

    /// Verifies that branch, reference, and promotion-set DTOs expose implemented visibility fields.
    [<Test>]
    member _.CurrentDtosExposeImplementedVisibilityAndOwnershipFields() =
        Assert.That(typeof<Grace.Types.Branch.BranchDto>.GetProperty ("Visibility"), Is.Not.Null)
        Assert.That(typeof<Grace.Types.Branch.BranchDto>.GetProperty ("Ownership"), Is.Not.Null)
        Assert.That(typeof<Grace.Types.Reference.ReferenceDto>.GetProperty ("Visibility"), Is.Not.Null)
        Assert.That(typeof<Grace.Types.Reference.ReferenceDto>.GetProperty ("Ownership"), Is.Not.Null)
        Assert.That(typeof<Grace.Types.PromotionSet.PromotionSetDto>.GetProperty ("Visibility"), Is.Not.Null)
        Assert.That(typeof<Grace.Types.PromotionSet.PromotionSetDto>.GetProperty ("Ownership"), Is.Not.Null)

    /// Verifies that content-object contracts do not gain global visibility or ownership fields.
    [<Test>]
    member _.ContentObjectContractsDoNotExposeGlobalVisibilityOrOwnershipFields() =
        Assert.That(typeof<FileVersion>.GetProperty ("Visibility"), Is.Null)
        Assert.That(typeof<FileVersion>.GetProperty ("Ownership"), Is.Null)
        Assert.That(typeof<DirectoryVersion>.GetProperty ("Visibility"), Is.Null)
        Assert.That(typeof<DirectoryVersion>.GetProperty ("Ownership"), Is.Null)

    /// Verifies that implemented create surfaces expose visibility inputs while unrelated routes do not accept no-ops.
    [<Test>]
    member _.CurrentCreateParametersExposeImplementedVisibilityAndOwnershipFields() =
        Assert.That(typeof<CreateBranchParameters>.GetProperty ("Visibility"), Is.Not.Null)
        Assert.That(typeof<CreateBranchParameters>.GetProperty ("Ownership"), Is.Not.Null)
        Assert.That(typeof<CreatePromotionSetParameters>.GetProperty ("Visibility"), Is.Not.Null)
        Assert.That(typeof<CreatePromotionSetParameters>.GetProperty ("Ownership"), Is.Not.Null)

    /// Verifies that ordinal and advertised visibility defaults do not fail open.
    [<Test>]
    member _.VisibilityDefaultsFailClosed() =
        let firstCase = FSharpType.GetUnionCases(typeof<ResourceVisibility>)[0]

        Assert.That(firstCase.Name, Is.EqualTo(nameof ResourceVisibility.Private))
        Assert.That(ResourceVisibility.Default, Is.EqualTo(ResourceVisibility.Private))

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

    /// Verifies that visibility helpers compose with parameter types that already inherit route identity.
    [<Test>]
    member _.SharedVisibilityHelpersComposeWithRouteParameterBases() =
        let parameters = BranchCreateVisibilityParameters()
        parameters.OwnerName <- "owner"
        parameters.OrganizationName <- "organization"
        parameters.RepositoryName <- "repository"
        parameters.BranchName <- "main"
        parameters.Visibility <- "SecurityEmbargoed"
        parameters.Ownership <- "ContributorOwned"

        Assert.That(parameters.OwnerName, Is.EqualTo("owner"))
        Assert.That(parameters.OrganizationName, Is.EqualTo("organization"))
        Assert.That(parameters.RepositoryName, Is.EqualTo("repository"))
        Assert.That(parameters.BranchName, Is.EqualTo("main"))

        Assert.That(
            tryParseVisibilityInput parameters.Visibility
            |> Option.isNone,
            Is.True
        )

        Assert.That(tryParseOwnershipInput parameters.Ownership, Is.EqualTo(Some ResourceOwnership.ContributorOwned))

    /// Verifies that ownership helpers reject arbitrary ownership at the public boundary.
    [<Test>]
    member _.SharedOwnershipHelpersRejectArbitraryOwnershipAtPublicBoundary() =
        Assert.That(tryParseVisibilityInput "Private", Is.EqualTo(Some ResourceVisibility.Private))

        Assert.That(
            tryParseOwnershipInput "OwnerId:11111111-1111-1111-1111-111111111111"
            |> Option.isNone,
            Is.True
        )
