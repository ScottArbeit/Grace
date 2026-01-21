namespace Grace.Authorization.Tests

open FsCheck
open Grace.Types.PersonalAccessToken
open NUnit.Framework
open System

[<Parallelizable(ParallelScope.All)>]
type PersonalAccessTokenTests() =

    [<Test>]
    member _.RoundTripParsesFormattedToken() =
        let userId = "user-123"
        let tokenId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        let secret = Array.init 32 (fun index -> byte (index + 1))

        let token = formatToken userId tokenId secret

        match tryParseToken token with
        | None -> Assert.Fail("Expected token to parse.")
        | Some(parsedUserId, parsedTokenId, parsedSecret) ->
            Assert.That(parsedUserId, Is.EqualTo(userId))
            Assert.That(parsedTokenId, Is.EqualTo(tokenId))
            Assert.That(parsedSecret, Is.EquivalentTo(secret))

    [<Test>]
    member _.TryParseNeverThrows() =
        let property (input: string) =
            try
                tryParseToken input |> ignore
                true
            with
            | _ -> false

        Check.QuickThrowOnFailure property

    [<Test>]
    member _.RejectsMalformedTokens() =
        let tokenId = Guid.NewGuid().ToString("N")
        let goodUser = "user-123"
        let goodSecret = Convert.ToBase64String(Array.init 32 (fun i -> byte (i + 1))).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        let goodUserB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(goodUser)).TrimEnd('=').Replace('+', '-').Replace('/', '_')

        let malformed =
            [
                ""
                " "
                "not-a-token"
                $"{TokenPrefix}{goodUserB64}.{tokenId}" // missing segment
                $"{TokenPrefix}{goodUserB64}.{Guid.NewGuid()}.{goodSecret}" // wrong guid format
                $"{TokenPrefix}@@@.{tokenId}.{goodSecret}" // invalid user b64
                $"{TokenPrefix}{goodUserB64}.{tokenId}.@@@" // invalid secret b64
                $"{TokenPrefix}{goodUserB64}.{tokenId}.abcd" // wrong secret length
            ]

        for value in malformed do
            Assert.That(tryParseToken value, Is.EqualTo(None), $"Expected malformed token to be rejected: {value}")
