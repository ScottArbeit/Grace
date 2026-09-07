namespace Grace.Actors

open System
open System.Security.Cryptography
open System.Text

/// Signs repository-bound Library cursors and retained-content read descriptors.
module LibraryTokens =

    /// Encodes bytes with the URL-safe base64 alphabet and no padding.
    let private encode bytes =
        Convert
            .ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    /// Decodes URL-safe base64 without accepting malformed input.
    let private tryDecode (value: string) =
        try
            let padded =
                value.Replace('-', '+').Replace('_', '/')
                + String.replicate ((4 - value.Length % 4) % 4) "="

            Some(Convert.FromBase64String padded)
        with
        | :? FormatException -> None

    /// Signs one purpose-scoped payload with HMAC-SHA256.
    let sign (key: byte array) purpose payload =
        let body = Encoding.UTF8.GetBytes($"{purpose}|{payload}")
        use hmac = new HMACSHA256(key)
        $"{encode body}.{encode (hmac.ComputeHash body)}"

    /// Returns a signed payload only when syntax, signature, and purpose all match.
    let tryVerify (key: byte array) purpose (token: string) =
        match token.Split('.', StringSplitOptions.None) with
        | [| encodedBody; encodedSignature |] ->
            match tryDecode encodedBody, tryDecode encodedSignature with
            | Some body, Some signature ->
                use hmac = new HMACSHA256(key)
                let expected = hmac.ComputeHash body
                let text = Encoding.UTF8.GetString body
                let prefix = purpose + "|"

                if
                    CryptographicOperations.FixedTimeEquals(signature, expected)
                    && text.StartsWith(prefix, StringComparison.Ordinal)
                then
                    Some(text.Substring(prefix.Length))
                else
                    None
            | _ -> None
        | _ -> None

    /// Encodes one committed repository position for public synchronization responses.
    let cursor key repositoryId epoch value = sign key "cursor" $"{repositoryId:D}|{epoch:D}|{value}"

    /// Encodes a short-lived retained-content descriptor without adding another durable grant lifecycle.
    let contentRead key repositoryId itemId contentVersionId expiresUnixSeconds =
        sign key "content" $"{repositoryId:D}|{itemId:D}|{contentVersionId:D}|{expiresUnixSeconds}"

    /// Validates and parses a retained-content descriptor at the supplied wall-clock boundary.
    let tryContentRead key nowUnixSeconds token =
        match tryVerify key "content" token with
        | Some payload ->
            match payload.Split('|', StringSplitOptions.None) with
            | [| repository; item; content; expires |] ->
                match Guid.TryParse repository, Guid.TryParse item, Guid.TryParse content, Int64.TryParse expires with
                | (true, repositoryId), (true, itemId), (true, contentVersionId), (true, expiresAt) when expiresAt > nowUnixSeconds ->
                    Some(repositoryId, itemId, contentVersionId)
                | _ -> None
            | _ -> None
        | None -> None
