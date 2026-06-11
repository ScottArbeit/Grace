namespace Grace.Types.Tests

open Grace.Types.ContentBlockMetadata
open Microsoft.FSharp.Reflection
open NUnit.Framework
open Orleans

[<TestFixture>]
type ContentBlockMetadataTypesTests() =

    [<Test>]
    member _.ContentBlockMetadataCommandCasesHaveStableSerializerIds() =
        let actual =
            FSharpType.GetUnionCases(typeof<ContentBlockMetadataCommand>)
            |> Array.map (fun unionCase ->
                let serializerId =
                    unionCase.GetCustomAttributes()
                    |> Seq.choose (function
                        | :? IdAttribute as attribute -> Some attribute
                        | _ -> None)
                    |> Seq.exactlyOne

                unionCase.Name, serializerId.Id)

        Assert.That(
            actual,
            Is.EquivalentTo(
                [|
                    "ReplaceWholeRecord", 0u
                    "MergePhysicalRanges", 1u
                    "CompactPhysicalRanges", 2u
                    "SetCompactionChurnState", 3u
                |]
            )
        )
