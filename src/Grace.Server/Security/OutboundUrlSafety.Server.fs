namespace Grace.Server.Security

open Grace.Shared.Utilities
open Grace.Types.Webhooks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open System
open System.Collections.Generic
open System.Globalization
open System.Net
open System.Security.Cryptography
open System.Text

module OutboundUrlSafety =

    [<Literal>]
    let UnsafeLocalDevelopmentConfigKey = "grace__webhooks__outbound_urls__allow_unsafe_local_development"

    type RedirectPolicy =
        | RefuseRedirects
        | RevalidateEveryRedirect

    type ValidationFailure =
        | MissingUrl
        | InvalidUri
        | UnsupportedScheme of string
        | HttpsRequired
        | EmbeddedCredentialsRejected
        | FragmentRejected
        | HostRequired
        | LocalTargetRequiresDevelopmentAcknowledgement
        | UnsafeHostRejected of string

    type ValidationRequest =
        {
            Url: string
            RequestedSafety: OutboundUrlSafety
            AcknowledgeUnsafeLocalDevelopment: bool
        }

        static member PublicHttps url = { Url = url; RequestedSafety = OutboundUrlSafety.PublicHttps; AcknowledgeUnsafeLocalDevelopment = false }

    type ValidatedOutboundUrl = { Uri: Uri; ScopedUrl: ScopedOutboundUrl; RedirectPolicy: RedirectPolicy; ResolvedAddresses: IPAddress array }

    type SigningInput = { RequestId: string; Timestamp: string; KeyId: string; PayloadSha256Hex: string; SignedMaterial: byte array }

    type HostAddressResolver = string -> IPAddress array

    let private tryGetConfigValue (configuration: IConfiguration) (name: string) =
        if isNull configuration then
            None
        else
            let value = configuration[getConfigKey name]
            if String.IsNullOrWhiteSpace value then None else Some(value.Trim())

    let isUnsafeLocalDevelopmentEnabled (configuration: IConfiguration) =
        match tryGetConfigValue configuration UnsafeLocalDevelopmentConfigKey with
        | Some value ->
            String.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || String.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || String.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
        | None -> false

    let isDevelopmentHostEnvironment (hostEnvironment: IHostEnvironment) = if isNull hostEnvironment then false else hostEnvironment.IsDevelopment()

    let private isLocalhostName (host: string) = String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)

    let private tryParseIPAddress (host: string) =
        let mutable address = Unchecked.defaultof<IPAddress>

        if IPAddress.TryParse(host, &address) then Some address else None

    let private isIPv4InRange (address: IPAddress) (prefix: byte array) (prefixLength: int) =
        let bytes = address.GetAddressBytes()

        if bytes.Length <> 4 then
            false
        else
            let mutable remainingBits = prefixLength
            let mutable matches = true
            let mutable index = 0

            while matches && remainingBits > 0 do
                let bitsInByte = Math.Min(remainingBits, 8)
                let mask = byte (0xFF <<< (8 - bitsInByte))
                matches <- (bytes[index] &&& mask) = (prefix[index] &&& mask)
                remainingBits <- remainingBits - bitsInByte
                index <- index + 1

            matches

    let private isIPv6InRange (address: IPAddress) (prefix: byte array) (prefixLength: int) =
        let bytes = address.GetAddressBytes()

        if bytes.Length <> 16 then
            false
        else
            let mutable remainingBits = prefixLength
            let mutable matches = true
            let mutable index = 0

            while matches && remainingBits > 0 do
                let bitsInByte = Math.Min(remainingBits, 8)
                let mask = byte (0xFF <<< (8 - bitsInByte))
                matches <- (bytes[index] &&& mask) = (prefix[index] &&& mask)
                remainingBits <- remainingBits - bitsInByte
                index <- index + 1

            matches

    let private isUnsafeIPv4Address (address: IPAddress) =
        isIPv4InRange address [| 0uy; 0uy; 0uy; 0uy |] 8
        || isIPv4InRange address [| 10uy; 0uy; 0uy; 0uy |] 8
        || isIPv4InRange address [| 100uy; 64uy; 0uy; 0uy |] 10
        || isIPv4InRange address [| 127uy; 0uy; 0uy; 0uy |] 8
        || isIPv4InRange address [| 169uy; 254uy; 0uy; 0uy |] 16
        || isIPv4InRange address [| 172uy; 16uy; 0uy; 0uy |] 12
        || isIPv4InRange address [| 192uy; 0uy; 0uy; 0uy |] 24
        || isIPv4InRange address [| 192uy; 0uy; 2uy; 0uy |] 24
        || isIPv4InRange address [| 192uy; 88uy; 99uy; 0uy |] 24
        || isIPv4InRange address [| 192uy; 168uy; 0uy; 0uy |] 16
        || isIPv4InRange address [| 198uy; 18uy; 0uy; 0uy |] 15
        || isIPv4InRange address [| 198uy; 51uy; 100uy; 0uy |] 24
        || isIPv4InRange address [| 203uy; 0uy; 113uy; 0uy |] 24
        || isIPv4InRange address [| 224uy; 0uy; 0uy; 0uy |] 4
        || isIPv4InRange address [| 240uy; 0uy; 0uy; 0uy |] 4

    let private isUnsafeIPv6Address (address: IPAddress) =
        isIPv6InRange
            address
            [|
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
            |]
            128
        || isIPv6InRange
            address
            [|
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                1uy
            |]
            128
        || isIPv6InRange
            address
            [|
                0uy
                0x64uy
                0xFFuy
                0x9Buy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
            |]
            96
        || isIPv6InRange
            address
            [|
                0uy
                0x64uy
                0xFFuy
                0x9Buy
                0uy
                1uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
            |]
            48
        || isIPv6InRange
            address
            [|
                0x20uy
                0x01uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
            |]
            32
        || isIPv6InRange
            address
            [|
                0x20uy
                0x02uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
            |]
            16
        || isIPv6InRange
            address
            [|
                0xFCuy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
            |]
            7
        || isIPv6InRange
            address
            [|
                0xFEuy
                0x80uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
            |]
            10
        || isIPv6InRange
            address
            [|
                0xFFuy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
                0uy
            |]
            8

    let private normalizeAddress (address: IPAddress) = if address.IsIPv4MappedToIPv6 then address.MapToIPv4() else address

    let private isUnsafeAddress (address: IPAddress) =
        let normalizedAddress = normalizeAddress address

        match normalizedAddress.AddressFamily with
        | Net.Sockets.AddressFamily.InterNetwork -> isUnsafeIPv4Address normalizedAddress
        | Net.Sockets.AddressFamily.InterNetworkV6 -> isUnsafeIPv6Address normalizedAddress
        | _ -> true

    let private addressForFailure (address: IPAddress) = (normalizeAddress address).ToString()

    let private resolveHostAddresses: HostAddressResolver = fun host -> Dns.GetHostAddresses(host)

    let private normalizeAddresses (addresses: IPAddress array) =
        addresses
        |> Array.map normalizeAddress
        |> Array.distinctBy (fun address -> address.ToString())

    let private validateResolvedAddresses host (addresses: IPAddress array) =
        if isNull addresses || addresses.Length = 0 then
            Error(UnsafeHostRejected host)
        else
            let normalizedAddresses = normalizeAddresses addresses

            match normalizedAddresses
                  |> Array.tryFind isUnsafeAddress
                with
            | Some address -> Error(UnsafeHostRejected(addressForFailure address))
            | None -> Ok normalizedAddresses

    let private validateResolvedHost (resolver: HostAddressResolver) host =
        try
            resolver host |> validateResolvedAddresses host
        with
        | :? Net.Sockets.SocketException -> Error(UnsafeHostRejected host)
        | :? ArgumentException -> Error(UnsafeHostRejected host)

    let private isExactPermittedLoopbackAddress (address: IPAddress) =
        let normalizedAddress = normalizeAddress address

        normalizedAddress.Equals(IPAddress.Parse("127.0.0.1"))
        || normalizedAddress.Equals(IPAddress.IPv6Loopback)

    let private isPermittedUnsafeLocalHost (uri: Uri) =
        match tryParseIPAddress uri.Host with
        | Some address -> isExactPermittedLoopbackAddress address
        | None -> isLocalhostName uri.Host

    let private validatePublicTarget (resolver: HostAddressResolver) (uri: Uri) =
        if uri.Scheme <> Uri.UriSchemeHttps then
            Error HttpsRequired
        elif isLocalhostName uri.Host then
            Error(UnsafeHostRejected "localhost")
        else
            match tryParseIPAddress uri.Host with
            | Some address when isUnsafeAddress address -> Error(UnsafeHostRejected(addressForFailure address))
            | Some address -> Ok [| normalizeAddress address |]
            | None -> validateResolvedHost resolver uri.Host

    let private validateLocalDevelopmentTarget (isDevelopmentHostEnvironment: bool) (configuration: IConfiguration) (request: ValidationRequest) (uri: Uri) =
        if
            not
                (
                    isDevelopmentHostEnvironment
                    && isUnsafeLocalDevelopmentEnabled configuration
                    && request.AcknowledgeUnsafeLocalDevelopment
                )
        then
            Error LocalTargetRequiresDevelopmentAcknowledgement
        elif uri.Scheme <> Uri.UriSchemeHttp
             && uri.Scheme <> Uri.UriSchemeHttps then
            Error(UnsupportedScheme uri.Scheme)
        elif isPermittedUnsafeLocalHost uri then
            match tryParseIPAddress uri.Host with
            | Some address -> Ok [| normalizeAddress address |]
            | None ->
                Ok [| IPAddress.Parse("127.0.0.1")
                      IPAddress.IPv6Loopback |]
        else
            Error(UnsafeHostRejected uri.Host)

    let validateWithResolver (resolver: HostAddressResolver) (isDevelopmentHostEnvironment: bool) (configuration: IConfiguration) (request: ValidationRequest) =
        if String.IsNullOrWhiteSpace request.Url then
            Error MissingUrl
        else
            let trimmedUrl = request.Url.Trim()
            let mutable uri = Unchecked.defaultof<Uri>

            if not (Uri.TryCreate(trimmedUrl, UriKind.Absolute, &uri)) then
                Error InvalidUri
            elif uri.Scheme <> Uri.UriSchemeHttp
                 && uri.Scheme <> Uri.UriSchemeHttps then
                Error(UnsupportedScheme uri.Scheme)
            elif not (String.IsNullOrEmpty uri.UserInfo) then
                Error EmbeddedCredentialsRejected
            elif not (String.IsNullOrEmpty uri.Fragment) then
                Error FragmentRejected
            elif String.IsNullOrWhiteSpace uri.Host then
                Error HostRequired
            else
                let validation =
                    match request.RequestedSafety with
                    | OutboundUrlSafety.PublicHttps -> validatePublicTarget resolver uri
                    | OutboundUrlSafety.LocalUnsafeDevOnly -> validateLocalDevelopmentTarget isDevelopmentHostEnvironment configuration request uri

                validation
                |> Result.map (fun resolvedAddresses ->
                    {
                        Uri = uri
                        ScopedUrl = { Url = uri.AbsoluteUri; Safety = request.RequestedSafety }
                        RedirectPolicy = RedirectPolicy.RevalidateEveryRedirect
                        ResolvedAddresses = resolvedAddresses
                    })

    let validate (hostEnvironment: IHostEnvironment) (configuration: IConfiguration) (request: ValidationRequest) =
        validateWithResolver resolveHostAddresses (isDevelopmentHostEnvironment hostEnvironment) configuration request

    let validateRedirect (hostEnvironment: IHostEnvironment) (configuration: IConfiguration) (original: ValidatedOutboundUrl) (redirectUri: Uri) =
        validate
            hostEnvironment
            configuration
            {
                Url = redirectUri.AbsoluteUri
                RequestedSafety = original.ScopedUrl.Safety
                AcknowledgeUnsafeLocalDevelopment = original.ScopedUrl.Safety = OutboundUrlSafety.LocalUnsafeDevOnly
            }

    module Redaction =

        let private sensitiveQueryNames =
            HashSet<string>(
                [
                    "api_key"
                    "apikey"
                    "sig"
                    "signature"
                    "token"
                    "oauth_token"
                    "access_token"
                    "refresh_token"
                    "id_token"
                    "code"
                    "state"
                    "nonce"
                    "samlrequest"
                    "samlresponse"
                    "relaystate"
                    "session_state"
                    "authenticity_token"
                    "csrf_token"
                    "xsrf_token"
                    "request_token"
                    "verification_token"
                    "key"
                    "x-api-key"
                    "secret"
                    "client_secret"
                    "password"
                    "x-amz-signature"
                    "x-amz-credential"
                    "x-amz-security-token"
                    "sharedaccesssignature"
                    "se"
                    "sp"
                    "sv"
                    "sr"
                    "skoid"
                    "sktid"
                    "skt"
                    "ske"
                    "sks"
                    "skv"
                ],
                StringComparer.OrdinalIgnoreCase
            )

        let private redactQuery (query: string) =
            if String.IsNullOrEmpty query then
                String.Empty
            else
                query
                    .TrimStart('?')
                    .Split([| '&'; ';' |], StringSplitOptions.None)
                |> Array.map (fun part ->
                    let separator = part.IndexOf('=')
                    let name = if separator < 0 then part else part.Substring(0, separator)

                    if sensitiveQueryNames.Contains(Uri.UnescapeDataString(name)) then
                        $"{name}=REDACTED"
                    else
                        part)
                |> String.concat "&"

        let redactUri (value: string) =
            if String.IsNullOrWhiteSpace value then
                value
            else
                let mutable uri = Unchecked.defaultof<Uri>

                if Uri.TryCreate(value, UriKind.Absolute, &uri) then
                    let builder = UriBuilder(uri)

                    if not (String.IsNullOrEmpty uri.UserInfo) then
                        builder.UserName <- "REDACTED"
                        builder.Password <- String.Empty

                    builder.Query <- redactQuery uri.Query
                    builder.Fragment <- String.Empty
                    builder.Uri.AbsoluteUri
                else
                    "[invalid-uri-redacted]"

    module Signing =

        let sha256Hex (payload: byte array) =
            let hash = SHA256.HashData payload

            hash
            |> Array.map (fun value -> value.ToString("x2", CultureInfo.InvariantCulture))
            |> String.concat String.Empty

        let createSigningInput (requestId: string) (timestamp: string) (keyId: string) (payload: byte array) =
            if String.IsNullOrWhiteSpace requestId then
                invalidArg (nameof requestId) "A delivery or request id is required for outbound signing."

            if String.IsNullOrWhiteSpace timestamp then
                invalidArg (nameof timestamp) "A timestamp is required for outbound signing."

            if String.IsNullOrWhiteSpace keyId then
                invalidArg (nameof keyId) "A key id or secret version is required for outbound signing."

            if isNull payload then nullArg (nameof payload)

            let payloadHash = sha256Hex payload
            let material = $"{requestId.Trim()}.{timestamp.Trim()}.{keyId.Trim()}.{payloadHash}"

            {
                RequestId = requestId.Trim()
                Timestamp = timestamp.Trim()
                KeyId = keyId.Trim()
                PayloadSha256Hex = payloadHash
                SignedMaterial = Encoding.UTF8.GetBytes material
            }
