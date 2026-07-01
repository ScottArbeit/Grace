namespace Grace.Types

open NodaTime
open Orleans
open System
open System.Text

/// Contains personal access token helpers.
module PersonalAccessToken =
    [<Literal>]
    let TokenPrefix = "grace_pat_v1_"

    /// Represents personal access token id.
    type PersonalAccessTokenId = Guid

    /// Represents personal access token summary.
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

    /// Represents personal access token created.
    [<GenerateSerializer>]
    type PersonalAccessTokenCreated =
        {
            [<Id 0u>]
            Token: string
            [<Id 1u>]
            Summary: PersonalAccessTokenSummary
        }

    /// Represents personal access token validation result.
    [<GenerateSerializer>]
    type PersonalAccessTokenValidationResult =
        {
            [<Id 0u>]
            TokenId: PersonalAccessTokenId
            [<Id 1u>]
            UserId: Common.UserId
            [<Id 2u>]
            Claims: string list
            [<Id 3u>]
            GroupIds: string list
        }

    /// Encodes token bytes with URL-safe Base64 and no padding.
    let private base64UrlEncode (bytes: byte array) =
        Convert
            .ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    /// Attempts to base64 url decode.
    let private tryBase64UrlDecode (value: string) =
        try
            let normalized = value.Replace('-', '+').Replace('_', '/')
            let padding = (4 - (normalized.Length % 4)) % 4
            let padded = normalized + String.replicate padding "="
            Some(Convert.FromBase64String(padded))
        with
        | _ -> None

    /// Formats token.
    let formatToken (userId: string) (tokenId: Guid) (secret: byte array) =
        let userIdBytes = Encoding.UTF8.GetBytes(userId)
        let userIdB64 = base64UrlEncode userIdBytes
        let tokenIdN = tokenId.ToString("N")
        let secretB64 = base64UrlEncode secret
        $"{TokenPrefix}{userIdB64}.{tokenIdN}.{secretB64}"

    /// Attempts to parse token.
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
