namespace Grace.Server.Tests

open Grace.Shared.Utilities
open Grace.Types.Policy
open Grace.Types.Types
open NUnit.Framework
open System
open System.Security.Cryptography
open System.Text

[<Parallelizable(ParallelScope.All)>]
type PolicyDeterminism() =
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

    let computeSnapshotId (parserVersion: string) (policyBlock: string) =
        let bytes = Encoding.UTF8.GetBytes($"{parserVersion}\n{policyBlock}")
        let hashBytes = SHA256.HashData(bytes)
        Sha256Hash(byteArrayToString (hashBytes.AsSpan()))

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

    [<Test>]
    member _.SnapshotIdStableForSameContent() =
        let policyBlock = "version: 1\nqueue:\n  onFailure: pause"
        let parserVersion = "v1"
        let first = computeSnapshotId parserVersion policyBlock
        let second = computeSnapshotId parserVersion policyBlock
        Assert.That(first, Is.EqualTo(second))

    [<Test>]
    member _.SnapshotIdChangesWhenParserVersionChanges() =
        let policyBlock = "version: 1\nqueue:\n  onFailure: pause"
        let first = computeSnapshotId "v1" policyBlock
        let second = computeSnapshotId "v2" policyBlock
        Assert.That(first, Is.Not.EqualTo(second))

    [<Test>]
    member _.SnapshotIdChangesWhenPolicyChanges() =
        let parserVersion = "v1"
        let first = computeSnapshotId parserVersion "version: 1\nqueue:\n  onFailure: pause"
        let second = computeSnapshotId parserVersion "version: 1\nqueue:\n  onFailure: continue"
        Assert.That(first, Is.Not.EqualTo(second))
