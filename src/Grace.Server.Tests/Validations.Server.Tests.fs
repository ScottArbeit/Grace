namespace Grace.Server.Tests

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open FsUnit
open NUnit.Framework
open System
open Microsoft.FSharp.Core

[<Parallelizable(ParallelScope.All)>]
type Validations() =

    [<Test>]
    member this.``valid Guid returns Ok``() =
        let result = (Guid.isValidAndNotEmptyGuid "6fddb3c1-24c2-4e2e-8f57-98d0838c0c3f" "error").Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    [<Test>]
    member this.``empty string for guid returns Ok``() =
        let result = (Guid.isValidAndNotEmptyGuid "" "error").Result
        Assert.That(result, Is.EqualTo(Common.okResult))

    [<Test>]
    member this.``invalid Guid returns Error``() =
        let result = (Guid.isValidAndNotEmptyGuid "not a Guid" "error").Result
        Assert.That(result, Is.EqualTo(Common.errorResult))

    [<Test>]
    member this.``Guid Empty returns Error``() =
        let result = (Guid.isValidAndNotEmptyGuid (Guid.Empty.ToString()) "error").Result
        Assert.That(result, Is.EqualTo(Common.errorResult))

    [<Test>]
    member this.``Guid is not Guid Empty returns Ok``() =
        let result = (Guid.isNotEmpty (Guid.NewGuid()) "error").Result
        Assert.That(result, Is.EqualTo(Common.okResult))

    [<Test>]
    member this.``Guid is Guid Empty returns Error``() =
        let result = (Guid.isNotEmpty Guid.Empty "error").Result
        Assert.That(result, Is.EqualTo(Common.errorResult))

    [<Test>]
    member this.``positive number returns Ok``() =
        let result = (Number.isPositiveOrZero 5.0 "error").Result
        Assert.That(result, Is.EqualTo(Common.okResult))

    [<Test>]
    member this.``zero returns Ok``() =
        let result = (Number.isPositiveOrZero 0.0 "error").Result
        Assert.That(result, Is.EqualTo(Common.okResult))

    [<Test>]
    member this.``negative number returns Error``() =
        let result = (Number.isPositiveOrZero (-5.0) "error").Result
        Assert.That(result, Is.EqualTo(Common.errorResult))

    [<Test>]
    member this.``number within range returns Ok``() =
        let result = (Number.isWithinRange 4 0 10 "error").Result
        Assert.That(result, Is.EqualTo(Common.okResult))

    [<Test>]
    member this.``number not within range returns Error``() =
        let result = (Number.isWithinRange 20 0 10 "error").Result
        Assert.That(result, Is.EqualTo(Common.errorResult))

    [<Test>]
    member this.``not empty string returns Ok``() =
        let result = (String.isNotEmpty "not empty" "error").Result
        Assert.That(result, Is.EqualTo(Common.okResult))

    [<Test>]
    member this.``empty string returns Error``() =
        let result = (String.isNotEmpty "" "error").Result
        Assert.That(result, Is.EqualTo(Common.errorResult))

    [<Test>]
    member this.``valid SHA-256 hash returns Ok``() =
        let result =
            (String.isEmptyOrValidSha256Hash "67A1790DCA55B8803AD024EE28F616A284DF5DD7B8BA5F68B4B252A5E925AF79" "error")
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    [<Test>]
    member this.``empty string for SHA-256 value returns Ok``() =
        let result = (String.isEmptyOrValidSha256Hash "" "error").Result
        Assert.That(result, Is.EqualTo(Common.okResult))

    [<Test>]
    member this.``invalid SHA-256 hash returns Error``() =
        let result = (String.isValidSha256Hash "not a SHA-256 hash" "error").Result
        Assert.That(result, Is.EqualTo(Common.errorResult))

    [<Test>]
    member this.``string length less than max length returns Ok``() =
        let result = (String.maxLength "a string" 10 "error").Result
        Assert.That(result, Is.EqualTo(Common.okResult))

    [<Test>]
    member this.``string length equal to max length returns Ok``() =
        let result = (String.maxLength "a string" 8 "error").Result
        Assert.That(result, Is.EqualTo(Common.okResult))

    [<Test>]
    member this.``string length greater than max length returns Error``() =
        let result = (String.maxLength "a string" 1 "error").Result
        Assert.That(result, Is.EqualTo(Common.errorResult))

    [<Test>]
    member this.``string is member of discriminated union returns Ok``() =
        let result =
            (DiscriminatedUnion.isMemberOf<Types.ReferenceType, string> "Checkpoint" "error")
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    [<Test>]
    member this.``string is not member of discriminated union returns Error``() =
        let result =
            (DiscriminatedUnion.isMemberOf<Types.ReferenceType, string> "Not a member" "error")
                .Result

        Assert.That(result, Is.EqualTo(Common.errorResult))

    [<Test>]
    member this.``either id or name is provided returns Ok``() =
        let result = (Input.eitherIdOrNameMustBeProvided "id" "" "error").Result
        Assert.That(result, Is.EqualTo(Common.okResult))

    [<Test>]
    member this.``both id and name are provided returns Ok``() =
        let result = (Input.eitherIdOrNameMustBeProvided "id" "name" "error").Result
        Assert.That(result, Is.EqualTo(Common.okResult))

    [<Test>]
    member this.``neither id nor name is provided returns Error``() =
        let result = (Input.eitherIdOrNameMustBeProvided "" "" "error").Result
        Assert.That(result, Is.EqualTo(Common.errorResult))

    [<Test>]
    member this.``non-empty list returns Ok``() =
        let result = (Input.listIsNonEmpty [ 1; 2; 3 ] "error").Result
        Assert.That(result, Is.EqualTo(Common.okResult))

    [<Test>]
    member this.``empty list returns Error``() =
        let result = (Input.listIsNonEmpty [] "error").Result
        Assert.That(result, Is.EqualTo(Common.errorResult))
