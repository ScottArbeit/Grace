namespace Grace.Server.Tests

open Grace.Shared.Utilities
open Grace.Types.Policy
open Grace.Types.Common
open NUnit.Framework
open System
open System.Security.Cryptography
open System.Text

/// Covers policy Determinism behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type PolicyDeterminism() =
    /// Builds extract Policy Block test data for the server unit policy Determinism scenarios in this file.
    let extractPolicyBlock (markdown: string) =
        let startMarker = "```grace-policy"
        let startIndex = markdown.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase)

        if startIndex < 0 then
            None
        else
            let afterStart = markdown.IndexOf('\n', startIndex)

            if afterStart < 0 then
                None
            else
                let endIndex = markdown.IndexOf("```", afterStart + 1, StringComparison.Ordinal)

                if endIndex < 0 then
                    None
                else
                    markdown
                        .Substring(afterStart + 1, endIndex - afterStart - 1)
                        .Trim()
                    |> Some

    /// Computes snapshot Id deterministically so the server unit policy Determinism assertions can compare stable values.
    let computeSnapshotId (parserVersion: string) (policyBlock: string) =
        let bytes = Encoding.UTF8.GetBytes($"{parserVersion}\n{policyBlock}")
        let hashBytes = SHA256.HashData(bytes)
        Sha256Hash(byteArrayToString (hashBytes.AsSpan()))

    /// Verifies that extract Policy Block Returns Block Contents.
    [<Test>]
    member _.ExtractPolicyBlockReturnsBlockContents() =
        let markdown =
            String.concat
                "\n"
                [
                    "# Grace Instructions"
                    "Intro text."
                    "```grace-policy"
                    "version: 1"
                    "defaults: {}"
                    "```"
                    "More details."
                ]

        let result = extractPolicyBlock markdown
        Assert.That(result, Is.EqualTo(Some("version: 1\ndefaults: {}")))

    /// Verifies that snapshot Id Stable For Same Content.
    [<Test>]
    member _.SnapshotIdStableForSameContent() =
        let policyBlock = "version: 1\nqueue:\n  onFailure: pause"
        let parserVersion = "v1"
        let first = computeSnapshotId parserVersion policyBlock
        let second = computeSnapshotId parserVersion policyBlock
        Assert.That(first, Is.EqualTo(second))

    /// Verifies that snapshot Id Changes When Parser Version Changes.
    [<Test>]
    member _.SnapshotIdChangesWhenParserVersionChanges() =
        let policyBlock = "version: 1\nqueue:\n  onFailure: pause"
        let first = computeSnapshotId "v1" policyBlock
        let second = computeSnapshotId "v2" policyBlock
        Assert.That(first, Is.Not.EqualTo(second))

    /// Verifies that snapshot Id Changes When Policy Changes.
    [<Test>]
    member _.SnapshotIdChangesWhenPolicyChanges() =
        let parserVersion = "v1"
        let first = computeSnapshotId parserVersion "version: 1\nqueue:\n  onFailure: pause"
        let second = computeSnapshotId parserVersion "version: 1\nqueue:\n  onFailure: continue"
        Assert.That(first, Is.Not.EqualTo(second))
