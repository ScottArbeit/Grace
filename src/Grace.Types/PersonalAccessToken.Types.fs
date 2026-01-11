namespace Grace.Types

open NodaTime
open Orleans
open System
open System.Text

module PersonalAccessToken =
    [<Literal>]
    let TokenPrefix = "grace_pat_v1_"

    type PersonalAccessTokenId = Guid

    [<GenerateSerializer>]
    type PersonalAccessTokenSummary =
        {
            [<Id 0u>]
            TokenId: PersonalAccessTokenId
            [<Id 1u>]
            Name: string
            [<Id 2u>]
            CreatedAt: Instant
            [<Id 3u>]
            ExpiresAt: Instant option
            [<Id 4u>]
            LastUsedAt: Instant option
            [<Id 5u>]
            RevokedAt: Instant option
        }

    [<GenerateSerializer>]
    type PersonalAccessTokenCreated =
        {
            [<Id 0u>]
            Token: string
            [<Id 1u>]
            Summary: PersonalAccessTokenSummary
        }

    [<GenerateSerializer>]
    type PersonalAccessTokenValidationResult =
        {
            [<Id 0u>]
            TokenId: PersonalAccessTokenId
            [<Id 1u>]
            UserId: Types.UserId
            [<Id 2u>]
            Claims: string list
            [<Id 3u>]
            GroupIds: string list
        }

    let private base64UrlEncode (bytes: byte array) =
        Convert
            .ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    let private tryBase64UrlDecode (value: string) =
        try
            let normalized = value.Replace('-', '+').Replace('_', '/')
            let padding = (4 - (normalized.Length % 4)) % 4
            let padded = normalized + String.replicate padding "="
            Some(Convert.FromBase64String(padded))
        with
        | _ -> None

    let formatToken (userId: string) (tokenId: Guid) (secret: byte array) =
        let userIdBytes = Encoding.UTF8.GetBytes(userId)
        let userIdB64 = base64UrlEncode userIdBytes
        let tokenIdN = tokenId.ToString("N")
        let secretB64 = base64UrlEncode secret
        $"{TokenPrefix}{userIdB64}.{tokenIdN}.{secretB64}"

    let tryParseToken (token: string) =
        if String.IsNullOrWhiteSpace token then
            None
        else if not (token.StartsWith(TokenPrefix, StringComparison.Ordinal)) then
            None
        else
            let payload = token.Substring(TokenPrefix.Length)
            let parts = payload.Split('.', StringSplitOptions.RemoveEmptyEntries)

            if parts.Length <> 3 then
                None
            else
                let userIdPart = parts[0]
                let tokenIdPart = parts[1]
                let secretPart = parts[2]

                match Guid.TryParseExact(tokenIdPart, "N") with
                | true, tokenId ->
                    match tryBase64UrlDecode userIdPart, tryBase64UrlDecode secretPart with
                    | Some userIdBytes, Some secretBytes ->
                        let userId = Encoding.UTF8.GetString(userIdBytes)

                        if String.IsNullOrWhiteSpace userId
                           || secretBytes.Length <> 32 then
                            None
                        else
                            Some(userId, tokenId, secretBytes)
                    | _ -> None
                | _ -> None
