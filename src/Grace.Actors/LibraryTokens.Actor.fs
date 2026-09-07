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

    /// Validates and parses one repository-bound public cursor.
    let tryCursor key repositoryId token =
        match tryVerify key "cursor" token with
        | Some payload ->
            match payload.Split('|', StringSplitOptions.None) with
            | [| repository; epoch; value |] ->
                match Guid.TryParse repository, Guid.TryParse epoch, Int64.TryParse value with
                | (true, actualRepositoryId), (true, actualEpoch), (true, position) when actualRepositoryId = repositoryId -> Some(actualEpoch, position)
                | _ -> None
            | _ -> None
        | None -> None

    /// Encodes one expiring bootstrap or change-page continuation.
    let page key purpose repositoryId value offset expiresUnixSeconds = sign key ("page:" + purpose) $"{repositoryId:D}|{value}|{offset}|{expiresUnixSeconds}"

    /// Validates and parses one purpose-scoped page continuation.
    let tryPage key purpose repositoryId nowUnixSeconds token =
        match tryVerify key ("page:" + purpose) token with
        | Some payload ->
            let parts = payload.Split('|', StringSplitOptions.None)

            if parts.Length = 4 then
                match Guid.TryParse parts[0], Int32.TryParse parts[2], Int64.TryParse parts[3] with
                | (true, actualRepositoryId), (true, offset), (true, expiresAt) when
                    actualRepositoryId = repositoryId
                    && expiresAt > nowUnixSeconds
                    ->
                    Some(parts[1], offset)
                | _ -> None
            else
                None
        | None -> None

    /// Encodes a short-lived retained-content descriptor without adding another durable lifecycle.
    let contentRead key repositoryId itemId contentVersionId contentRevision expiresUnixSeconds =
        sign key "content" $"{repositoryId:D}|{itemId:D}|{contentVersionId:D}|{contentRevision}|{expiresUnixSeconds}"

    /// Validates and parses a retained-content descriptor at the supplied wall-clock boundary.
    let tryContentRead key nowUnixSeconds token =
        match tryVerify key "content" token with
        | Some payload ->
            match payload.Split('|', StringSplitOptions.None) with
            | [| repository; item; content; revision; expires |] ->
                match Guid.TryParse repository, Guid.TryParse item, Guid.TryParse content, Int64.TryParse expires with
                | (true, repositoryId), (true, itemId), (true, contentVersionId), (true, expiresAt) when expiresAt > nowUnixSeconds ->
                    Some(repositoryId, itemId, contentVersionId, revision)
                | _ -> None
            | _ -> None
        | None -> None
