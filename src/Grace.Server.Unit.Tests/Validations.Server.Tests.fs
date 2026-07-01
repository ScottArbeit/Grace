namespace Grace.Server.Tests

open Grace.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open FsUnit
open Microsoft.FSharp.Core
open NUnit.Framework
open System
open System.Threading.Tasks

/// Covers validations behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type Validations() =

    /// Verifies that valid Guid returns Ok.
    [<Test>]
    member this.``valid Guid returns Ok``() =
        let result =
            (Guid.isValidAndNotEmptyGuid "6fddb3c1-24c2-4e2e-8f57-98d0838c0c3f" TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    /// Verifies that empty string for guid returns Ok.
    [<Test>]
    member this.``empty string for guid returns Ok``() =
        let result =
            (Guid.isValidAndNotEmptyGuid "" TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    /// Verifies that invalid Guid returns Error.
    [<Test>]
    member this.``invalid Guid returns Error``() =
        let result =
            (Guid.isValidAndNotEmptyGuid "not a Guid" TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.errorResult))

    /// Verifies that guid Empty returns Error.
    [<Test>]
    member this.``Guid Empty returns Error``() =
        let result =
            (Guid.isValidAndNotEmptyGuid (Guid.Empty.ToString()) TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.errorResult))

    /// Verifies that guid is not Guid Empty returns Ok.
    [<Test>]
    member this.``Guid is not Guid Empty returns Ok``() =
        let result =
            (Guid.isNotEmpty (Guid.NewGuid()) TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    /// Verifies that guid is Guid Empty returns Error.
    [<Test>]
    member this.``Guid is Guid Empty returns Error``() =
        let result =
            (Guid.isNotEmpty Guid.Empty TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.errorResult))

    /// Verifies that positive number returns Ok.
    [<Test>]
    member this.``positive number returns Ok``() =
        let result =
            (Number.isPositiveOrZero 5.0 TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    /// Verifies that zero returns Ok.
    [<Test>]
    member this.``zero returns Ok``() =
        let result =
            (Number.isPositiveOrZero 0.0 TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    /// Verifies that negative number returns Error.
    [<Test>]
    member this.``negative number returns Error``() =
        let result =
            (Number.isPositiveOrZero (-5.0) TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.errorResult))

    /// Verifies that number within range returns Ok.
    [<Test>]
    member this.``number within range returns Ok``() =
        let result =
            (Number.isWithinRange 4 0 10 TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    /// Verifies that number not within range returns Error.
    [<Test>]
    member this.``number not within range returns Error``() =
        let result =
            (Number.isWithinRange 20 0 10 TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.errorResult))

    /// Verifies that not empty string returns Ok.
    [<Test>]
    member this.``not empty string returns Ok``() =
        let result =
            (String.isNotEmpty "not empty" TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    /// Verifies that empty string returns Error.
    [<Test>]
    member this.``empty string returns Error``() =
        let result = (String.isNotEmpty "" TestError.TestFailed).Result
        Assert.That(result, Is.EqualTo(Common.errorResult))

    /// Verifies that valid SHA-256 hash returns Ok.
    [<Test>]
    member this.``valid SHA-256 hash returns Ok``() =
        let result =
            (String.isEmptyOrValidSha256Hash "67A1790DCA55B8803AD024EE28F616A284DF5DD7B8BA5F68B4B252A5E925AF79" TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    /// Verifies that sHA-256 prefix boundary values return Ok.
    [<Test>]
    member this.``SHA-256 prefix boundary values return Ok``() =
        let cases =
            [|
                "2 characters", "67"
                "64 characters", "67a1790dca55b8803ad024ee28f616a284df5dd7b8ba5f68b4b252a5e925af79"
                "uppercase lookup", "67A1790DCA55B8803AD024EE28F616A284DF5DD7B8BA5F68B4B252A5E925AF79"
            |]

        for caseName, hash in cases do
            let result =
                (String.isValidSha256HashPrefix hash TestError.TestFailed)
                    .Result

            Assert.That(result, Is.EqualTo(Common.okResult), caseName)

    /// Verifies that sHA-256 version hash requires lowercase full value.
    [<Test>]
    member this.``SHA-256 version hash requires lowercase full value``() =
        let valid =
            (String.isValidSha256VersionHash "67a1790dca55b8803ad024ee28f616a284df5dd7b8ba5f68b4b252a5e925af79" VersionHashError.InvalidSha256VersionHash)
                .Result

        let uppercase =
            (String.isValidSha256VersionHash "67A1790DCA55B8803AD024EE28F616A284DF5DD7B8BA5F68B4B252A5E925AF79" VersionHashError.InvalidSha256VersionHash)
                .Result

        let short =
            (String.isValidSha256VersionHash "67" VersionHashError.InvalidSha256VersionHash)
                .Result

        Assert.That(Result.isOk valid, Is.True)
        Assert.That(Result.isError uppercase, Is.True)
        Assert.That(Result.isError short, Is.True)

    /// Verifies that empty string for SHA-256 value returns Ok.
    [<Test>]
    member this.``empty string for SHA-256 value returns Ok``() =
        let result =
            (String.isEmptyOrValidSha256Hash "" TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    /// Verifies that invalid SHA-256 hash returns Error.
    [<Test>]
    member this.``invalid SHA-256 hash returns Error``() =
        let result =
            (String.isValidSha256Hash "not a SHA-256 hash" TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.errorResult))

    /// Verifies that invalid SHA-256 prefix values return Error.
    [<Test>]
    member this.``invalid SHA-256 prefix values return Error``() =
        let cases =
            [|
                "empty", ""
                "one character", "6"
                "non-hex", "not-a-sha-256-hash"
            |]

        for caseName, hash in cases do
            let result =
                (String.isValidSha256HashPrefix hash TestError.TestFailed)
                    .Result

            Assert.That(result, Is.EqualTo(Common.errorResult), caseName)

    /// Verifies that valid BLAKE3 prefix values return Ok.
    [<Test>]
    member this.``valid BLAKE3 prefix values return Ok``() =
        let cases =
            [|
                "2 characters", "af"
                "64 characters", "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adcd1e8c76d9a8885f16a39f"
                "uppercase lookup", "AF1349B9F5F9A1A6A0404DEA36DCC9499BCB25C9ADCD1E8C76D9A8885F16A39F"
            |]

        for caseName, hash in cases do
            let result =
                (String.isValidBlake3HashPrefix hash VersionHashError.InvalidBlake3Hash)
                    .Result

            Assert.That(Result.isOk result, Is.True, caseName)

    /// Verifies that invalid BLAKE3 prefix values return Error.
    [<Test>]
    member this.``invalid BLAKE3 prefix values return Error``() =
        let cases =
            [|
                "empty", ""
                "one character", "a"
                "non-hex", "not-a-blake3-hash"
            |]

        for caseName, hash in cases do
            let result =
                (String.isValidBlake3HashPrefix hash VersionHashError.InvalidBlake3Hash)
                    .Result

            Assert.That(Result.isError result, Is.True, caseName)

    /// Verifies that empty string for optional BLAKE3 prefix returns Ok.
    [<Test>]
    member this.``empty string for optional BLAKE3 prefix returns Ok``() =
        let result =
            (String.isEmptyOrValidBlake3HashPrefix "" VersionHashError.InvalidBlake3Hash)
                .Result

        Assert.That(Result.isOk result, Is.True)

    /// Verifies that bLAKE3 version hash requires lowercase full value.
    [<Test>]
    member this.``BLAKE3 version hash requires lowercase full value``() =
        let valid =
            (String.isValidBlake3VersionHash "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adcd1e8c76d9a8885f16a39f" VersionHashError.InvalidBlake3Hash)
                .Result

        let uppercase =
            (String.isValidBlake3VersionHash "AF1349B9F5F9A1A6A0404DEA36DCC9499BCB25C9ADCD1E8C76D9A8885F16A39F" VersionHashError.InvalidBlake3Hash)
                .Result

        let empty =
            (String.isValidBlake3VersionHash "" VersionHashError.Blake3HashIsRequired)
                .Result

        let short =
            (String.isValidBlake3VersionHash "af" VersionHashError.InvalidBlake3Hash)
                .Result

        Assert.That(Result.isOk valid, Is.True)
        Assert.That(Result.isError uppercase, Is.True)
        Assert.That(Result.isError empty, Is.True)
        Assert.That(Result.isError short, Is.True)

    /// Verifies that version hash errors return localized text.
    [<Test>]
    member this.``version hash errors return localized text``() =
        Assert.That(
            getErrorMessage VersionHashError.InvalidBlake3Hash,
            Is.EqualTo("The provided BLAKE3 hash is not a valid lowercase 64-character BLAKE3 hash value.")
        )

        Assert.That(getErrorMessage VersionHashError.Blake3HashIsRequired, Is.EqualTo("The Blake3Hash value is required."))

    /// Verifies that string length less than max length returns Ok.
    [<Test>]
    member this.``string length less than max length returns Ok``() =
        let result =
            (String.maxLength "a string" 10 TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    /// Verifies that string length equal to max length returns Ok.
    [<Test>]
    member this.``string length equal to max length returns Ok``() =
        let result =
            (String.maxLength "a string" 8 TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    /// Verifies that string length greater than max length returns Error.
    [<Test>]
    member this.``string length greater than max length returns Error``() =
        let result =
            (String.maxLength "a string" 1 TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.errorResult))

    /// Verifies that string is member of discriminated union returns Ok.
    [<Test>]
    member this.``string is member of discriminated union returns Ok``() =
        let result =
            (DiscriminatedUnion.isMemberOf<Common.ReferenceType, TestError> "Checkpoint" TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    /// Verifies that string is not member of discriminated union returns Error.
    [<Test>]
    member this.``string is not member of discriminated union returns Error``() =
        let result =
            (DiscriminatedUnion.isMemberOf<Common.ReferenceType, TestError> "Not a member" TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.errorResult))

    /// Verifies that either id or name is provided returns Ok.
    [<Test>]
    member this.``either id or name is provided returns Ok``() =
        let result =
            (Input.eitherIdOrNameMustBeProvided "id" "" TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    /// Verifies that both id and name are provided returns Ok.
    [<Test>]
    member this.``both id and name are provided returns Ok``() =
        let result =
            (Input.eitherIdOrNameMustBeProvided "id" "name" TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    /// Verifies that neither id nor name is provided returns Error.
    [<Test>]
    member this.``neither id nor name is provided returns Error``() =
        let result =
            (Input.eitherIdOrNameMustBeProvided "" "" TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.errorResult))

    /// Verifies that non-empty list returns Ok.
    [<Test>]
    member this.``non-empty list returns Ok``() =
        let result =
            (Input.listIsNonEmpty [ 1; 2; 3 ] TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.okResult))

    /// Verifies that empty list returns Error.
    [<Test>]
    member this.``empty list returns Error``() =
        let result =
            (Input.listIsNonEmpty [] TestError.TestFailed)
                .Result

        Assert.That(result, Is.EqualTo(Common.errorResult))
